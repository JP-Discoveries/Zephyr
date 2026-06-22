using System.Diagnostics;
using System.IO;

namespace Zephyr.UI.Services;

public static class TerminalService
{
    public static void OpenAt(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (!TryLaunch("wt.exe",   $"-d \"{dir}\""))
        if (!TryLaunch("pwsh.exe", $"-NoExit -Command Set-Location '{dir}'"))
            TryLaunch("cmd.exe",   $"/k cd /d \"{dir}\"");
    }

    private static bool TryLaunch(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
