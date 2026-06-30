using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyr.UI.Dialogs;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Controls;

// Tab drag: reorder within the strip, drag to split the right pane, merge back, or
// drag out of the window to spawn a new window. Uses a low-level Win32 mouse hook to
// catch button-releases that happen outside our window, which WPF never reports.
public partial class FilePane
{
    // ── Tab drag ──────────────────────────────────────────────────────────────
    private Point            _tabDragStart;
    private TabViewModel?    _tabDragItem;
    private bool             _tabIsDragging;
    private DispatcherTimer? _tabDragTimer;

    [DllImport("user32.dll")] private static extern bool   GetCursorPos(out Win32Point pt);
    [DllImport("user32.dll")] private static extern short  GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)] private struct Win32Point { public int X, Y; }
    private const int VK_LBUTTON  = 0x01;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP_MSG = 0x0202;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr w, IntPtr l);
    private LowLevelMouseProc? _mouseHookProc;   // must hold reference to prevent GC
    private IntPtr              _mouseHook;

    private void TabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tabIsDragging) AbortTabDrag();
        _tabDragStart  = e.GetPosition(null);
        _tabDragItem   = HitTestTab(e.OriginalSource as DependencyObject);
        _tabIsDragging = false;
    }

    private void TabBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragItem == null) return;

        var diff = _tabDragStart - e.GetPosition(null);
        if (!_tabIsDragging &&
            Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (!_tabIsDragging)
        {
            _tabIsDragging = true;
            Mouse.Capture((IInputElement)sender);
            if (Window.GetWindow(this) is { } w)
                w.PreviewMouseLeftButtonUp += TabDrag_GlobalMouseUp;
            _tabDragTimer = new DispatcherTimer(DispatcherPriority.Input)
                { Interval = TimeSpan.FromMilliseconds(32) };
            _tabDragTimer.Tick += TabDragTimer_Tick;
            _tabDragTimer.Start();
            // Low-level hook receives WM_LBUTTONUP system-wide regardless of which window
            // has focus or capture — the only reliable way to catch outside-window releases.
            _mouseHookProc = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
        }

        // Live reorder within the tab strip
        var posInStrip = e.GetPosition(TabStrip);
        if (Pane != null && _tabDragItem != null &&
            posInStrip.X >= 0 && posInStrip.X <= TabStrip.ActualWidth &&
            posInStrip.Y >= 0 && posInStrip.Y <= TabStrip.ActualHeight)
        {
            var targetIdx  = GetTabIndexAtPoint(posInStrip);
            var currentIdx = Pane.Tabs.IndexOf(_tabDragItem);
            if (targetIdx >= 0 && currentIdx >= 0 && targetIdx != currentIdx)
                Pane.Tabs.Move(currentIdx, targetIdx);
        }

        UpdateDragCursor();
    }

    // Re-acquire capture if WPF drops it while the button is still held.
    // Use GetAsyncKeyState (hardware state) not Mouse.LeftButton (stale WPF cache).
    private void TabBar_LostCapture(object sender, MouseEventArgs e)
    {
        if (_tabIsDragging && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            Mouse.Capture((IInputElement)sender);
    }

    private void TabDragTimer_Tick(object? sender, EventArgs e)
    {
        if (!_tabIsDragging) { StopDragTimer(); return; }
        // GetAsyncKeyState reflects actual hardware state — Mouse.LeftButton only updates
        // when WM_LBUTTONUP reaches our window, which never happens for outside-window releases.
        bool lbDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!lbDown)
            CompleteDrag();
        else
            UpdateDragCursor();
    }

    private IntPtr MouseHookProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && w == (IntPtr)WM_LBUTTONUP_MSG && _tabIsDragging)
            Dispatcher.BeginInvoke(CompleteDrag, DispatcherPriority.Input);
        return CallNextHookEx(_mouseHook, code, w, l);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _mouseHookProc = null;
    }

    // WPF event path: fires when button released inside or (sometimes) outside window
    private void TabDrag_GlobalMouseUp(object sender, MouseButtonEventArgs e)
        => CompleteDrag();

    private void CompleteDrag()
    {
        if (!_tabIsDragging) return;    // guard against double-fire (hook + timer + event)
        _tabIsDragging = false;

        var tab = _tabDragItem;
        _tabDragItem = null;

        RemoveMouseHook();
        StopDragTimer();
        if (Window.GetWindow(this) is { } w) w.PreviewMouseLeftButtonUp -= TabDrag_GlobalMouseUp;
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;

        if (tab == null) return;

        var win = Window.GetWindow(this);
        if (win == null || win.DataContext is not MainViewModel vm) return;

        // Use Win32 GetCursorPos + PointToScreen for reliable DPI-correct
        // outside-window detection regardless of WPF coordinate mapping.
        GetCursorPos(out var sc);
        var tl = win.PointToScreen(new Point(0, 0));
        var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));

        bool outside   = sc.X < (int)tl.X || sc.X > (int)br.X
                      || sc.Y < (int)tl.Y || sc.Y > (int)br.Y;
        bool rightHalf = !outside && (sc.X - tl.X) > (br.X - tl.X) * 0.55;
        bool leftHalf  = !outside && !rightHalf;
        bool isRight   = Pane == vm.RightPane;

        if (outside)
        {
            var exe = Environment.ProcessPath
                   ?? Path.Combine(AppContext.BaseDirectory,
                      System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add(tab.CurrentPath ?? "");
                System.Diagnostics.Process.Start(psi);
                if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            }
            catch (Exception ex) { ZephyrMessageBox.Show($"Could not open new window:\n{exe}\n{ex.Message}", "Error"); }
        }
        else if (rightHalf && !isRight)
        {
            if (!vm.IsSplitView) vm.ToggleSplitViewCommand.Execute(null);
            vm.RightPane.AddTab(tab.CurrentPath);
            if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            vm.SetActivePane(vm.RightPane);
        }
        else if (leftHalf && isRight && vm.IsSplitView)
        {
            vm.LeftPane.AddTab(tab.CurrentPath);
            if (Pane?.Tabs.Count > 1) Pane.CloseTabCommand.Execute(tab);
            vm.IsSplitView = false;
            vm.SetActivePane(vm.LeftPane);
        }
        // else: tab reorder was applied live in PreviewMouseMove
    }

    private void UpdateDragCursor()
    {
        var win = Window.GetWindow(this);
        if (win == null) return;

        GetCursorPos(out var sc);
        var tl = win.PointToScreen(new Point(0, 0));
        var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));

        bool outside   = sc.X < (int)tl.X || sc.X > (int)br.X
                      || sc.Y < (int)tl.Y || sc.Y > (int)br.Y;
        bool isRight   = win.DataContext is MainViewModel vm0 && Pane == vm0.RightPane && vm0.IsSplitView;
        bool rightHalf = !outside && (sc.X - tl.X) > (br.X - tl.X) * 0.55;
        bool leftHalf  = !outside && !rightHalf;

        Mouse.OverrideCursor = outside                      ? Cursors.No
                             : (rightHalf && !isRight)      ? Cursors.SizeAll
                             : (leftHalf  &&  isRight)      ? Cursors.SizeAll
                             : null;
    }

    private void AbortTabDrag()
    {
        RemoveMouseHook();
        StopDragTimer();
        if (Window.GetWindow(this) is { } w) w.PreviewMouseLeftButtonUp -= TabDrag_GlobalMouseUp;
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        _tabDragItem   = null;
        _tabIsDragging = false;
    }

    private void StopDragTimer()
    {
        _tabDragTimer?.Stop();
        if (_tabDragTimer != null) { _tabDragTimer.Tick -= TabDragTimer_Tick; _tabDragTimer = null; }
    }

    private int GetTabIndexAtPoint(Point pointInTabStrip)
    {
        if (Pane == null) return -1;

        var hit = VisualTreeHelper.HitTest(TabStrip, pointInTabStrip);
        for (var el = (DependencyObject?)hit?.VisualHit; el != null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is FrameworkElement { DataContext: TabViewModel tab })
            {
                var idx = Pane.Tabs.IndexOf(tab);
                if (idx >= 0) return idx;
            }
        }

        for (int i = 0; i < Pane.Tabs.Count; i++)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var right = c.TransformToAncestor(TabStrip).Transform(new Point(c.ActualWidth, 0)).X;
            if (pointInTabStrip.X <= right) return i;
        }
        return Pane.Tabs.Count > 0 ? Pane.Tabs.Count - 1 : -1;
    }

    private static TabViewModel? HitTestTab(DependencyObject? source)
    {
        for (var el = source; el != null; el = VisualTreeHelper.GetParent(el))
            if (el is FrameworkElement { DataContext: TabViewModel tab }) return tab;
        return null;
    }
}
