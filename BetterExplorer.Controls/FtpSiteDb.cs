using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BetterExplorer.Controls;

/// <summary>
/// SQLite-backed store for FTP/FTPS/SFTP/SCP site entries.
/// Follows the same WAL / dual-connection pattern as <see cref="FolderSettingsDb"/>.
/// Passwords are stored DPAPI-encrypted so they are never persisted in plaintext.
/// </summary>
internal sealed class FtpSiteDb : IDisposable
{
    // ── Singleton ────────────────────────────────────────────────────────────

    private static readonly Lazy<FtpSiteDb> _lazy = new(() => new FtpSiteDb());
    public static FtpSiteDb Instance => _lazy.Value;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly SqliteConnection _readConnection;
    private readonly SqliteConnection _writeConnection;
    private readonly object           _readLock  = new();
    private readonly object           _writeLock = new();

    // ── Construction ─────────────────────────────────────────────────────────

    private FtpSiteDb()
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

        Exec(_writeConnection, "PRAGMA journal_mode=WAL;");
        Exec(_writeConnection, "PRAGMA synchronous=NORMAL;");

        EnsureSchema();
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
            CREATE TABLE IF NOT EXISTS FtpSites (
                Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                DisplayName      TEXT    NOT NULL DEFAULT '',
                Protocol         INTEGER NOT NULL DEFAULT 0,
                Host             TEXT    NOT NULL DEFAULT '',
                Port             INTEGER NOT NULL DEFAULT 21,
                Username         TEXT    NOT NULL DEFAULT '',
                EncryptedPassword TEXT   NOT NULL DEFAULT '',
                RemotePath       TEXT    NOT NULL DEFAULT '/',
                AcceptAnyHostKey INTEGER NOT NULL DEFAULT 0,
                HostFingerprint  TEXT    NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public List<FtpSiteEntry> LoadAll()
    {
        lock (_readLock)
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, DisplayName, Protocol, Host, Port, Username,
                       EncryptedPassword, RemotePath, AcceptAnyHostKey, HostFingerprint
                FROM FtpSites
                ORDER BY DisplayName COLLATE NOCASE;
                """;
            using var reader = cmd.ExecuteReader();
            var list = new List<FtpSiteEntry>();
            while (reader.Read())
            {
                list.Add(new FtpSiteEntry
                {
                    Id                = reader.GetInt32(0),
                    DisplayName       = reader.GetString(1),
                    Protocol          = (FtpProtocol)reader.GetInt32(2),
                    Host              = reader.GetString(3),
                    Port              = reader.GetInt32(4),
                    Username          = reader.GetString(5),
                    EncryptedPassword = reader.GetString(6),
                    RemotePath        = reader.GetString(7),
                    AcceptAnyHostKey  = reader.GetInt32(8) != 0,
                    HostFingerprint   = reader.GetString(9),
                });
            }
            return list;
        }
    }

    /// <summary>Inserts a new entry and sets its <see cref="FtpSiteEntry.Id"/>.</summary>
    public void Insert(FtpSiteEntry entry)
    {
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FtpSites
                    (DisplayName, Protocol, Host, Port, Username, EncryptedPassword,
                     RemotePath, AcceptAnyHostKey, HostFingerprint)
                VALUES
                    (@dn, @proto, @host, @port, @user, @pass,
                     @path, @anyKey, @fp);
                SELECT last_insert_rowid();
                """;
            BindEntry(cmd, entry);
            entry.Id = (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public void Update(FtpSiteEntry entry)
    {
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                UPDATE FtpSites SET
                    DisplayName       = @dn,
                    Protocol          = @proto,
                    Host              = @host,
                    Port              = @port,
                    Username          = @user,
                    EncryptedPassword = @pass,
                    RemotePath        = @path,
                    AcceptAnyHostKey  = @anyKey,
                    HostFingerprint   = @fp
                WHERE Id = @id;
                """;
            BindEntry(cmd, entry);
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(int id)
    {
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM FtpSites WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private static void BindEntry(SqliteCommand cmd, FtpSiteEntry e)
    {
        cmd.Parameters.AddWithValue("@dn",     e.DisplayName);
        cmd.Parameters.AddWithValue("@proto",  (int)e.Protocol);
        cmd.Parameters.AddWithValue("@host",   e.Host);
        cmd.Parameters.AddWithValue("@port",   e.Port);
        cmd.Parameters.AddWithValue("@user",   e.Username);
        cmd.Parameters.AddWithValue("@pass",   e.EncryptedPassword);
        cmd.Parameters.AddWithValue("@path",   e.RemotePath);
        cmd.Parameters.AddWithValue("@anyKey", e.AcceptAnyHostKey ? 1 : 0);
        cmd.Parameters.AddWithValue("@fp",     e.HostFingerprint);
    }

    // ── Password helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with DPAPI (current-user scope)
    /// and returns a Base64 string safe for storage in the database.
    /// </summary>
    public static string EncryptPassword(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var bytes     = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a Base64 DPAPI blob previously produced by <see cref="EncryptPassword"/>.
    /// Returns an empty string on any failure.
    /// </summary>
    public static string DecryptPassword(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;
        try
        {
            var bytes     = Convert.FromBase64String(encrypted);
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _readConnection.Dispose();
        _writeConnection.Dispose();
    }
}
