using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zephyr.UI.Services;

/// <summary>
/// Real Explorer is launched by the shell itself, so Win+E dismisses an open Start menu
/// for free. Zephyr comes up via a global hotkey / IFEO redirect instead, which leaves
/// Start floating on top of us — so we close it ourselves.
/// </summary>
public static class StartMenuInterop
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_SYSCOMMAND = 0x0112;
    private const int  SC_TASKLIST   = 0xF130; // toggles the Start menu

    // Win11 hosts Start, Search and the notification flyouts in separate shell processes;
    // each of them sits above Zephyr the same way.
    private static readonly string[] ShellFlyoutHosts =
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "ShellExperienceHost",
    };

    /// <summary>Closes the Start menu if it is currently the foreground window. No-op otherwise.</summary>
    public static void DismissIfOpen()
    {
        if (!IsShellFlyoutForeground()) return;

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        // SC_TASKLIST is a toggle, which is why we only send it when Start is actually up.
        PostMessage(tray, WM_SYSCOMMAND, new IntPtr(SC_TASKLIST), IntPtr.Zero);
    }

    private static bool IsShellFlyoutForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return ShellFlyoutHosts.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // process already gone
        }
    }
}
