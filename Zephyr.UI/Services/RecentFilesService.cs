using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Zephyr.Core.Models;

namespace Zephyr.UI.Services;

public static class RecentFilesService
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHAddToRecentDocs(uint uFlags, string pv);
    private const uint SHARD_PATHW = 0x00000003;

    public static void AddToRecentDocs(string path)
    {
        try { SHAddToRecentDocs(SHARD_PATHW, path); }
        catch { }
    }

    public static IReadOnlyList<RecentFileItem> GetRecentFiles(int maxCount = 15)
    {
        var recentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Recent");

        var result = new List<RecentFileItem>();
        try
        {
            var links = Directory.GetFiles(recentDir, "*.lnk")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(maxCount * 3);

            foreach (var lnkPath in links)
            {
                try
                {
                    var target = ParseLnkBinary(lnkPath);
                    if (target == null || !File.Exists(target)) continue;
                    if (result.Any(r => r.FullPath.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                    result.Add(new RecentFileItem { Name = Path.GetFileName(target), FullPath = target });
                    if (result.Count >= maxCount) break;
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    // Parse MS-SHLLINK binary format to extract the target path without COM.
    private static string? ParseLnkBinary(string lnkPath)
    {
        try
        {
            using var fs = new FileStream(lnkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (fs.Length < 76) return null;

            // Shell Link Header (76 bytes)
            if (br.ReadInt32() != 0x4C) return null; // HeaderSize must be 0x4C
            br.ReadBytes(16);                         // LinkCLSID
            uint linkFlags = br.ReadUInt32();
            br.ReadBytes(52);                         // remainder of header

            // Skip IDList if present (HasLinkTargetIDList = bit 0)
            if ((linkFlags & 0x0001) != 0)
                br.ReadBytes(br.ReadUInt16());

            // Parse LinkInfo if present (HasLinkInfo = bit 1)
            if ((linkFlags & 0x0002) != 0)
            {
                long linkInfoStart      = fs.Position;
                int  linkInfoSize       = br.ReadInt32();
                int  linkInfoHeaderSize = br.ReadInt32();
                uint linkInfoFlags      = br.ReadUInt32();
                br.ReadInt32(); // VolumeIDOffset
                int localBasePathOffset = br.ReadInt32();
                br.ReadInt32(); // CommonNetworkRelativeLinkOffset
                br.ReadInt32(); // CommonPathSuffixOffset

                int localBasePathOffsetUnicode = 0;
                if (linkInfoHeaderSize > 0x1C) // extended header has Unicode offsets
                    localBasePathOffsetUnicode = br.ReadInt32();

                // VolumeIDAndLocalBasePath flag (bit 0 of LinkInfoFlags) — local volume path
                if ((linkInfoFlags & 0x0001) != 0)
                {
                    // Prefer Unicode path
                    if (localBasePathOffsetUnicode > 0)
                    {
                        fs.Position = linkInfoStart + localBasePathOffsetUnicode;
                        var s = ReadUtf16NullTerminated(br);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    // Fall back to ANSI path
                    if (localBasePathOffset > 0)
                    {
                        fs.Position = linkInfoStart + localBasePathOffset;
                        var s = ReadAnsiNullTerminated(br);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }

            return null;
        }
        catch { return null; }
    }

    private static string ReadAnsiNullTerminated(BinaryReader br)
    {
        var sb = new StringBuilder();
        byte b;
        while ((b = br.ReadByte()) != 0) sb.Append((char)b);
        return sb.ToString();
    }

    private static string ReadUtf16NullTerminated(BinaryReader br)
    {
        var sb = new StringBuilder();
        ushort u;
        while ((u = br.ReadUInt16()) != 0) sb.Append((char)u);
        return sb.ToString();
    }
}
