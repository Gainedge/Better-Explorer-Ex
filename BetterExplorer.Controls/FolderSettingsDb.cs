using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BetterExplorer.ShellApi;
using Microsoft.Data.Sqlite;

namespace BetterExplorer.Controls;

// ── Data records ─────────────────────────────────────────────────────────────

/// <summary>Per-folder UI settings persisted in the SQLite store.</summary>
internal sealed record FolderSettings
{
    /// <summary>Ordered list of visible column keys with their widths.</summary>
    public List<ColumnRecord> Columns { get; init; } =
    [
        new("Name", 280),
        new("Date", 160),
        new("Type", 140),
        new("Size",  90),
    ];

    public string SortColumn    { get; init; } = "Name";
    public bool   SortAscending { get; init; } = true;
    public ShellViewMode ViewMode { get; init; } = ShellViewMode.Details;
    /// <summary>Column key to group by, or empty string for no grouping.</summary>
    public string GroupColumn   { get; init; } = string.Empty;
}

internal sealed record ColumnRecord(
    [property: JsonPropertyName("key")]   string Key,
    [property: JsonPropertyName("width")] double Width);

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// SQLite-backed store for per-folder UI settings with an in-memory write-through
/// cache. Cache hits are instant dictionary lookups with no I/O. SQLite reads for
/// cache misses are dispatched to a background thread (never blocks the UI thread).
/// SQLite writes update the cache synchronously then flush to disk on a background
/// thread, so saves never block the UI either.
/// </summary>
internal sealed class FolderSettingsDb : IDisposable
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    private static readonly Lazy<FolderSettingsDb> _lazy =
        new(() => new FolderSettingsDb());

    public static FolderSettingsDb Instance => _lazy.Value;

    // ── State ─────────────────────────────────────────────────────────────────

    // In-memory write-through cache keyed by NormalisePath(path).
    // Populated on first load from DB; updated synchronously on every Save so
    // that subsequent navigations to the same folder need no I/O at all.
    private readonly ConcurrentDictionary<string, FolderSettings> _cache = new();

    // Separate read/write connections so WAL mode lets them run concurrently.
    private readonly SqliteConnection _readConnection;
    private readonly SqliteConnection _writeConnection;
    // Serialises access to each individual connection (they are not thread-safe).
    private readonly object _readLock  = new();
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Construction ──────────────────────────────────────────────────────────

    private FolderSettingsDb()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BExplorerEx");
        Directory.CreateDirectory(dir);

        var cs = $"Data Source={Path.Combine(dir, "Settings.sqlite")};";

        _readConnection  = new SqliteConnection(cs);
        _readConnection.Open();

        _writeConnection = new SqliteConnection(cs);
        _writeConnection.Open();

        // WAL: readers and writers don't block each other.
        Exec(_writeConnection, "PRAGMA journal_mode=WAL;");
        Exec(_writeConnection, "PRAGMA synchronous=NORMAL;");

        EnsureSchema();
        MigrateSchema();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS FolderSettings (
                Path          TEXT NOT NULL PRIMARY KEY,
                Columns       TEXT NOT NULL DEFAULT '',
                SortColumn    TEXT NOT NULL DEFAULT 'Name',
                SortAscending INTEGER NOT NULL DEFAULT 1,
                ViewMode      INTEGER NOT NULL DEFAULT 3
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds columns introduced after the initial schema without data loss.</summary>
    private void MigrateSchema()
    {
        // SQLite does not support ADD COLUMN IF NOT EXISTS before 3.37, so guard with try/catch.
        try { Exec(_writeConnection, "ALTER TABLE FolderSettings ADD COLUMN GroupColumn TEXT NOT NULL DEFAULT '';"); }
        catch { /* column already exists — harmless */ }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns stored settings for <paramref name="path"/>.
    /// Cache hit (folder visited before): synchronous, zero I/O.
    /// Cache miss (first visit): SQLite read is offloaded to a background thread.
    /// </summary>
    public async Task<FolderSettings> LoadAsync(string path)
    {
        var key = NormalisePath(path);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // ConfigureAwait(true) resumes on the UI thread (DispatcherQueue) so the
        // caller can immediately update XAML-bound properties after the await.
        var settings = await Task.Run(() => ReadFromDb(key)).ConfigureAwait(true);
        _cache[key] = settings;
        return settings;
    }

    /// <summary>
    /// Persists <paramref name="settings"/> for <paramref name="path"/>.
    /// The in-memory cache is updated immediately (so the next read is instant);
    /// the SQLite write is fire-and-forget on a background thread.
    /// </summary>
    public void Save(string path, FolderSettings settings)
    {
        var key = NormalisePath(path);
        _cache[key] = settings;                                   // instant, no I/O

        var columnsJson = JsonSerializer.Serialize(settings.Columns, _jsonOpts);
        Task.Run(() => WriteToDb(key, columnsJson, settings));    // background flush
    }

    // ── Private DB helpers (always called off the UI thread) ─────────────────

    private FolderSettings ReadFromDb(string key)
    {
        lock (_readLock)
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT Columns, SortColumn, SortAscending, ViewMode, GroupColumn
                FROM FolderSettings WHERE Path = @path;
                """;
            cmd.Parameters.AddWithValue("@path", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new FolderSettings();

            var columnsJson   = reader.GetString(0);
            var sortColumn    = reader.GetString(1);
            var sortAscending = reader.GetInt32(2) != 0;
            var viewMode      = (ShellViewMode)reader.GetInt32(3);
            var groupColumn   = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            List<ColumnRecord>? columns = null;
            if (!string.IsNullOrWhiteSpace(columnsJson))
            {
                try { columns = JsonSerializer.Deserialize<List<ColumnRecord>>(columnsJson, _jsonOpts); }
                catch { /* fall back to defaults */ }
            }

            return new FolderSettings
            {
                Columns       = columns ?? new FolderSettings().Columns,
                SortColumn    = sortColumn,
                SortAscending = sortAscending,
                ViewMode      = viewMode,
                GroupColumn   = groupColumn,
            };
        }
    }

    private void WriteToDb(string key, string columnsJson, FolderSettings s)
    {
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FolderSettings (Path, Columns, SortColumn, SortAscending, ViewMode, GroupColumn)
                VALUES (@path, @columns, @sortCol, @sortAsc, @viewMode, @groupCol)
                ON CONFLICT(Path) DO UPDATE SET
                    Columns       = excluded.Columns,
                    SortColumn    = excluded.SortColumn,
                    SortAscending = excluded.SortAscending,
                    ViewMode      = excluded.ViewMode,
                    GroupColumn   = excluded.GroupColumn;
                """;
            cmd.Parameters.AddWithValue("@path",     key);
            cmd.Parameters.AddWithValue("@columns",  columnsJson);
            cmd.Parameters.AddWithValue("@sortCol",  s.SortColumn);
            cmd.Parameters.AddWithValue("@sortAsc",  s.SortAscending ? 1 : 0);
            cmd.Parameters.AddWithValue("@viewMode", (int)s.ViewMode);
            cmd.Parameters.AddWithValue("@groupCol", s.GroupColumn);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormalisePath(string path) =>
        path.TrimEnd('\\', '/').ToUpperInvariant();

    public void Dispose()
    {
        _readConnection.Dispose();
        _writeConnection.Dispose();
    }
}
