using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zephyr.Core.FileSystem;

public enum LinkKind
{
    /// <summary>NTFS symbolic link (file or folder). Needs admin rights or Developer Mode.</summary>
    Symbolic,
    /// <summary>Directory junction (folders only, same machine). No elevation required.</summary>
    Junction,
    /// <summary>Hard link (files only, same volume). No elevation required.</summary>
    HardLink,
}

/// <summary>Creates NTFS links — symbolic links, directory junctions and hard links.</summary>
public static class LinkService
{
    public static void Create(LinkKind kind, string linkPath, string targetPath)
    {
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
            throw new IOException($"\"{Path.GetFileName(linkPath)}\" already exists at that location.");

        switch (kind)
        {
            case LinkKind.Symbolic:
                if (Directory.Exists(targetPath)) Directory.CreateSymbolicLink(linkPath, targetPath);
                else                              File.CreateSymbolicLink(linkPath, targetPath);
                break;

            case LinkKind.Junction:
                CreateJunction(linkPath, targetPath);
                break;

            case LinkKind.HardLink:
                if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
                    throw new IOException(
                        "Could not create the hard link. Hard links only work for files on the same drive.");
                break;
        }
    }

    /// <summary>Which link kinds make sense for the given target.</summary>
    public static IReadOnlyList<LinkKind> KindsFor(string targetPath) =>
        Directory.Exists(targetPath)
            ? [LinkKind.Junction, LinkKind.Symbolic]   // folders
            : [LinkKind.HardLink, LinkKind.Symbolic];  // files

    // Junctions are created via mklink (a cmd built-in) — robust and needs no elevation.
    private static void CreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        using var p = Process.Start(psi) ?? throw new IOException("Could not start mklink.");
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(err) ? "Junction creation failed." : err.Trim());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
