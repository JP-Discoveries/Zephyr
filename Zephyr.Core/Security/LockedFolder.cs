namespace Zephyr.Core.Security;

/// <summary>
/// A persisted folder-lock record. Stores only a PBKDF2 hash of the password —
/// never the plaintext. There is intentionally no recovery path: lose the
/// password and the folder stays gated in Zephyr (it remains reachable via Explorer,
/// since this is a UI gate, not on-disk encryption).
/// </summary>
public class LockedFolder
{
    /// <summary>Full path of the locked folder root. Descendants are gated too.</summary>
    public string Path       { get; set; } = string.Empty;
    /// <summary>Base64 random salt.</summary>
    public string Salt       { get; set; } = string.Empty;
    /// <summary>Base64 PBKDF2-SHA256 derived hash of the password.</summary>
    public string Hash       { get; set; } = string.Empty;
    /// <summary>PBKDF2 iteration count used to derive <see cref="Hash"/>.</summary>
    public int    Iterations { get; set; } = 100_000;
}
