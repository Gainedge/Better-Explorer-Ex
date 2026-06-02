using System;

namespace BetterExplorer.Controls;

/// <summary>Supported remote-access protocols.</summary>
public enum FtpProtocol
{
    Ftp  = 0,
    Ftps = 1,
    Sftp = 2,
    Scp  = 3,
}

/// <summary>
/// A single saved remote-site entry (FTP / FTPS / SFTP / SCP).
/// Passwords are stored as DPAPI-encrypted Base64 strings in the database and
/// decrypted on demand so they never live as plaintext in memory longer than needed.
/// </summary>
public sealed class FtpSiteEntry
{
    public int        Id          { get; set; }
    public string     DisplayName { get; set; } = string.Empty;
    public FtpProtocol Protocol   { get; set; } = FtpProtocol.Ftp;
    public string     Host        { get; set; } = string.Empty;
    public int        Port        { get; set; } = 21;
    public string     Username    { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted Base64 password as stored in the DB.
    /// Use <see cref="FtpSiteDb.DecryptPassword"/> to get the plaintext.
    /// </summary>
    public string     EncryptedPassword { get; set; } = string.Empty;

    /// <summary>Initial remote directory; empty means the server default.</summary>
    public string     RemotePath  { get; set; } = "/";

    /// <summary>Accept any host key without verification (insecure – user opt-in).</summary>
    public bool       AcceptAnyHostKey { get; set; } = false;

    /// <summary>Known-host fingerprint (SHA-256) for SFTP/SCP; empty means no pinning.</summary>
    public string     HostFingerprint { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}
