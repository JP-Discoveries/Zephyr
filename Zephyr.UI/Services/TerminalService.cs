using System.Diagnostics;
using System.IO;

namespace Zephyr.UI.Services;

public static class TerminalService
{
    public static void OpenAt(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        // Windows Terminal auto-creates a profile named "PowerShell" when PS7 is
        // installed, so -p is only safe to pass when pwsh.exe actually exists.
        var wtArgs = HasPwsh() ? $"-p \"PowerShell\" -d \"{dir}\"" : $"-d \"{dir}\"";
        if (!TryLaunch("wt.exe",   wtArgs))
        if (!TryLaunch("pwsh.exe", $"-NoExit -Command Set-Location '{dir}'"))
            TryLaunch("cmd.exe",   $"/k cd /d \"{dir}\"");
    }

    private static bool HasPwsh()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (File.Exists(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"))) return true;
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        return pathDirs.Any(d => !string.IsNullOrWhiteSpace(d)
                                 && File.Exists(Path.Combine(d.Trim(), "pwsh.exe")));
    }

    private static bool TryLaunch(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
