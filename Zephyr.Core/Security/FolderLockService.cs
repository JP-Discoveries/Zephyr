using System.Security.Cryptography;
using Zephyr.Core.Settings;

namespace Zephyr.Core.Security;

/// <summary>
/// App-wide registry of locked folders plus an in-memory set of folders the user has
/// unlocked for the current session. A UI gate (Option A): contents stay on disk
/// untouched and reachable via Explorer; Zephyr just prompts before showing them.
///
/// Unlock state lives only in memory, so everything re-locks when the app closes.
/// Passwords are stored as PBKDF2-SHA256 hashes and are never recoverable.
/// </summary>
public static class FolderLockService
{
    private const int    HashBytes      = 32;
    private const int    SaltBytes      = 16;
    private const int    DefaultIters   = 100_000;

    private static readonly List<LockedFolder> _locks = [];
    // Locked roots unlocked for this session (keyed by normalized path).
    private static readonly HashSet<string> _unlocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seed the registry from persisted settings. Call once at startup.</summary>
    public static void Load(IEnumerable<LockedFolder>? saved)
    {
        _locks.Clear();
        _unlocked.Clear();
        if (saved != null) _locks.AddRange(saved);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────
    private static string Normalize(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); } catch { }
        return path.TrimEnd('\\', '/');
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="root"/> or sits beneath it.</summary>
    private static bool IsAtOrUnder(string child, string root)
    {
        if (child.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return child.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ── Queries ───────────────────────────────────────────────────────────────
    /// <summary>True when this exact path is a locked root (drives the lock badge).</summary>
    public static bool IsLockRoot(string path)
    {
        var n = Normalize(path);
        return _locks.Any(l => Normalize(l.Path).Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The nearest locked root governing <paramref name="path"/> (self or ancestor), or null.</summary>
    public static LockedFolder? FindLockRoot(string path)
    {
        var n = Normalize(path);
        // Prefer the deepest matching root so nested locks behave predictably.
        return _locks
            .Where(l => IsAtOrUnder(n, Normalize(l.Path)))
            .OrderByDescending(l => Normalize(l.Path).Length)
            .FirstOrDefault();
    }

    /// <summary>True when a locked root governs this path and it has been unlocked this session.</summary>
    public static bool IsUnlocked(string lockRootPath) =>
        _unlocked.Contains(Normalize(lockRootPath));

    /// <summary>True when navigating to <paramref name="path"/> should require a password right now.</summary>
    public static bool IsGated(string path)
    {
        var root = FindLockRoot(path);
        return root != null && !IsUnlocked(root.Path);
    }

    // ── Mutations ─────────────────────────────────────────────────────────────
    /// <summary>Lock a folder with a fresh password hash and mark it unlocked for this session.</summary>
    public static void Lock(string path, string password)
    {
        var n = Normalize(path);
        _locks.RemoveAll(l => Normalize(l.Path).Equals(n, StringComparison.OrdinalIgnoreCase));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, DefaultIters);
        _locks.Add(new LockedFolder
        {
            Path       = n,
            Salt       = Convert.ToBase64String(salt),
            Hash       = Convert.ToBase64String(hash),
            Iterations = DefaultIters,
        });
        _unlocked.Add(n);   // you just set it — no need to re-enter immediately
        Persist();
    }

    /// <summary>Verify a password against a locked root; on success mark it unlocked for this session.</summary>
    public static bool Unlock(LockedFolder root, string password)
    {
        if (!Verify(root, password)) return false;
        _unlocked.Add(Normalize(root.Path));
        return true;
    }

    /// <summary>Re-lock a folder now (drops the session unlock). No password needed.</summary>
    public static void Relock(string path) => _unlocked.Remove(Normalize(path));

    /// <summary>Remove a lock entirely after verifying its password. Returns false on wrong password.</summary>
    public static bool RemoveLock(string path, string password)
    {
        var root = FindLockRoot(path);
        if (root == null) return true;
        if (!Verify(root, password)) return false;
        var n = Normalize(root.Path);
        _locks.RemoveAll(l => Normalize(l.Path).Equals(n, StringComparison.OrdinalIgnoreCase));
        _unlocked.Remove(n);
        Persist();
        return true;
    }

    // ── Hashing ───────────────────────────────────────────────────────────────
    public static bool Verify(LockedFolder root, string password)
    {
        try
        {
            var salt     = Convert.FromBase64String(root.Salt);
            var expected = Convert.FromBase64String(root.Hash);
            var actual   = Derive(password, salt, root.Iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static void Persist()
    {
        SettingsService.Current.LockedFolders = [.. _locks];
        SettingsService.Save(SettingsService.Current);
    }
}
