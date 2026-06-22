using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zephyr.UI.Services;

public static class ShellIntegrationService
{
    private const string DirKey = @"SOFTWARE\Classes\Directory\shell\Zephyr";
    private const string BgKey  = @"SOFTWARE\Classes\Directory\Background\shell\Zephyr";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DirKey);
        return key != null;
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName;
        WriteEntry(DirKey, exe, "%1");
        WriteEntry(BgKey,  exe, "%V");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(DirKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(BgKey,  throwOnMissingSubKey: false);
    }

    private static void WriteEntry(string baseKey, string exe, string pathToken)
    {
        using var key = Registry.CurrentUser.CreateSubKey(baseKey);
        key.SetValue("", "Open in Zephyr");
        key.SetValue("Icon", $"\"{exe}\"");
        using var cmd = key.CreateSubKey("command");
        cmd.SetValue("", $"\"{exe}\" \"{pathToken}\"");
    }

    // ── Default File Manager ──────────────────────────────────────────────────

    private const string FolderOpenKey    = @"SOFTWARE\Classes\Folder\shell\open\command";
    private const string DirectoryOpenKey = @"SOFTWARE\Classes\Directory\shell\open\command";
    private const string DriveOpenKey     = @"SOFTWARE\Classes\Drive\shell\open\command";
    private const string StartupRunKey    = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsDefaultFileManager()
    {
        using var folder = Registry.CurrentUser.OpenSubKey(FolderOpenKey);
        return folder?.GetValue("") is string v && v.Contains("Zephyr", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAsDefaultFileManager()
    {
        var exe = ResolveExePath();
        SetOpenCommand(FolderOpenKey,    exe);
        SetOpenCommand(DirectoryOpenKey, exe);
        SetOpenCommand(DriveOpenKey,     exe);
    }

    public static void RemoveDefaultFileManager()
    {
        ClearOpenCommand(FolderOpenKey);
        ClearOpenCommand(DirectoryOpenKey);
        ClearOpenCommand(DriveOpenKey);
    }

    public static bool IsLaunchAtStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKey);
        return key?.GetValue("Zephyr") != null;
    }

    public static void SetLaunchAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue("Zephyr", $"\"{ResolveExePath()}\"");
        else
            key.DeleteValue("Zephyr", throwOnMissingValue: false);
    }

    // ── Win+E background helper ───────────────────────────────────────────────
    // A hidden PowerShell process installs its own WH_KEYBOARD_LL hook at login.
    // This fires before Windows processes Win+E, so it works even when Zephyr
    // is closed and bypasses any IFEO entries left by other file managers.

    private static string DataDir   => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zephyr");
    private static string HelperPs1 => Path.Combine(DataDir, "ZephyrHotkey.ps1");
    private static string HelperVbs => Path.Combine(DataDir, "ZephyrHotkey.vbs");
    private static string HelperStop => Path.Combine(DataDir, "ZephyrHotkey.stop");
    private const  string HelperRunValue = "ZephyrHotkeyHelper";

    public static bool IsWinEHelperInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKey);
        return key?.GetValue(HelperRunValue) != null;
    }

    public static void InstallWinEHelper()
    {
        var exe = ResolveExePath();
        Directory.CreateDirectory(DataDir);

        // Signal any running helper to exit so it releases its hotkey registration
        try { File.WriteAllText(HelperStop, ""); } catch { }

        File.WriteAllText(HelperPs1, BuildHelperPs1(exe));
        File.WriteAllText(HelperVbs, BuildHelperVbs(HelperPs1));

        using var run = Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: true);
        run?.SetValue(HelperRunValue, $"wscript.exe \"{HelperVbs}\"");

        // Wait for the old helper to process and delete the stop file, then start fresh
        System.Threading.Thread.Sleep(2500);
        try { if (File.Exists(HelperStop)) File.Delete(HelperStop); } catch { }

        try
        {
            Process.Start(new ProcessStartInfo("wscript.exe", $"\"{HelperVbs}\"")
                { UseShellExecute = true });
        }
        catch { }
    }

    public static void UninstallWinEHelper()
    {
        using var run = Registry.CurrentUser.OpenSubKey(StartupRunKey, writable: true);
        run?.DeleteValue(HelperRunValue, throwOnMissingValue: false);

        // Signal any running helper to exit (it polls this file every 2 s)
        try { File.WriteAllText(HelperStop, ""); } catch { }
    }

    private static string BuildHelperPs1(string exePath)
    {
        // C# template uses $@"..." (interpolated verbatim):
        //   {{ / }} → literal { / } in output  (needed for all C# braces inside)
        //   ""      → literal " in output       (needed for DllImport strings etc.)
        //   @""     → @"  (opens PS1 double-quoted here-string)
        //   ""@     → "@  (closes PS1 double-quoted here-string, must be at column 0)
        // Inside the PS1 @"..."@ here-string, " is already literal — no extra escaping.
        var safeExe = exePath.Replace("'", "''"); // escape for PS1 single-quoted string
        return $@"Add-Type -TypeDefinition @""
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class ZephyrHotkeyHelper : Form {{
    [DllImport(""user32.dll"")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport(""user32.dll"")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport(""user32.dll"")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport(""user32.dll"")] static extern bool ShowWindow(IntPtr hWnd, int nCmd);
    const uint MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000, VK_E = 0x45;
    const int  WM_HOTKEY = 0x0312, HOTKEY_ID = 1;
    bool _registered;
    string _exe; string _stop;
    System.Windows.Forms.Timer _poll;
    public ZephyrHotkeyHelper(string exe, string stop) {{
        _exe = exe; _stop = stop;
        _registered = RegisterHotKey(Handle, HOTKEY_ID, MOD_WIN | MOD_NOREPEAT, VK_E);
        _poll = new System.Windows.Forms.Timer(); _poll.Interval = 2000;
        _poll.Tick += OnTick;
        _poll.Start();
    }}
    void OnTick(object s, EventArgs ev) {{
        if (File.Exists(_stop)) {{ File.Delete(_stop); Application.Exit(); return; }}
        if (!_registered)
            _registered = RegisterHotKey(Handle, HOTKEY_ID, MOD_WIN | MOD_NOREPEAT, VK_E);
    }}
    protected override void SetVisibleCore(bool v) {{ base.SetVisibleCore(false); }}
    protected override void OnFormClosed(FormClosedEventArgs e) {{
        UnregisterHotKey(Handle, HOTKEY_ID); _poll.Stop(); base.OnFormClosed(e);
    }}
    protected override void WndProc(ref Message m) {{
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) {{
            var procs = Process.GetProcessesByName(""Zephyr"");
            if (procs.Length > 0) {{
                ShowWindow(procs[0].MainWindowHandle, 9);
                SetForegroundWindow(procs[0].MainWindowHandle);
            }} else {{
                Process.Start(new ProcessStartInfo(_exe) {{ UseShellExecute = true }});
            }}
        }}
        base.WndProc(ref m);
    }}
}}
""@ -ReferencedAssemblies 'System.Windows.Forms.dll'
$stop = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) 'ZephyrHotkey.stop'
$h = New-Object ZephyrHotkeyHelper -ArgumentList @('{safeExe}', $stop)
[System.Windows.Forms.Application]::Run($h)
";
    }

    private static string BuildHelperVbs(string ps1Path) =>
        "Dim cmd\r\n" +
        $"cmd = \"powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \" & Chr(34) & \"{ps1Path}\" & Chr(34)\r\n" +
        "CreateObject(\"WScript.Shell\").Run cmd, 0, False\r\n";

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static string ResolveExePath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Zephyr.UI.exe");
        if (File.Exists(candidate)) return candidate;
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule!.FileName;
    }

    private static void SetOpenCommand(string keyPath, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("", $"\"{exe}\" \"%1\"");
    }

    private static void ClearOpenCommand(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue("") is string v && v.Contains("Zephyr", StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("", throwOnMissingValue: false);
    }

    // ── Shell actions ─────────────────────────────────────────────────────────

    public static void CreateShortcut(string targetPath, string destFolder)
    {
        var stem = Path.GetFileNameWithoutExtension(targetPath);
        var lnk  = Path.Combine(destFolder, $"{stem} - Shortcut.lnk");
        int n = 2;
        while (File.Exists(lnk))
            lnk = Path.Combine(destFolder, $"{stem} - Shortcut ({n++}).lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM object not available.");
        dynamic shell    = Activator.CreateInstance(shellType)!;
        var     shortcut = shell.CreateShortcut(lnk);
        shortcut.TargetPath  = targetPath;
        shortcut.Description = Path.GetFileName(targetPath);
        shortcut.Save();
    }

    public static void PinToStart(string path)
    {
        try
        {
            var sei = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask  = 0x0C,
                lpVerb = "pintostartscreen",
                lpFile = path,
                nShow  = 0,
            };
            ShellExecuteExW(ref sei);
        }
        catch { }
    }

    public static void ShowProperties(string path)
    {
        var sei = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask  = 0x0C,
            lpVerb = "properties",
            lpFile = path,
            nShow  = 5
        };
        ShellExecuteExW(ref sei);
    }

    public static void ShowOpenWith(string path)
    {
        var info = new OPENASINFO
        {
            pcszFile    = path,
            pcszClass   = null,
            oaifInFlags = 0x00000001 | 0x00000004  // OAIF_ALLOW_REGISTRATION | OAIF_EXEC
        };
        SHOpenWithDialog(IntPtr.Zero, ref info);
    }

    public static void RunAsAdmin(string path)
    {
        var sei = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask  = 0,
            lpVerb = "runas",
            lpFile = path,
            nShow  = 1
        };
        if (!ShellExecuteExW(ref sei))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1223) // ERROR_CANCELLED — user dismissed UAC
                throw new System.ComponentModel.Win32Exception(err);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO pExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string  pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass;
        public uint oaifInFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int    cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string  lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string  lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int    nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint   dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }
}
