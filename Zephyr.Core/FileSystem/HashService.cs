using System.Security.Cryptography;

namespace Zephyr.Core.FileSystem;

/// <summary>The hex digests of a file, computed in a single read pass. Values are lowercase.</summary>
public sealed record FileHashes(string Md5, string Sha1, string Sha256);

public static class HashService
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Streams the file once, feeding MD5, SHA-1 and SHA-256 in parallel. Reports fractional
    /// progress (0–1) and honours cancellation between reads.
    /// </summary>
    public static async Task<FileHashes> ComputeAsync(
        string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var md5    = MD5.Create();
        using var sha1   = SHA1.Create();
        using var sha256 = SHA256.Create();

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        long total  = stream.Length;
        long done   = 0;
        var  buffer = new byte[BufferSize];

        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha1.TransformBlock(buffer, 0, read, null, 0);
            sha256.TransformBlock(buffer, 0, read, null, 0);

            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }

        md5.TransformFinalBlock([], 0, 0);
        sha1.TransformFinalBlock([], 0, 0);
        sha256.TransformFinalBlock([], 0, 0);

        if (total == 0) progress?.Report(1.0);

        return new FileHashes(
            Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
            Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
            Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
    }
}
