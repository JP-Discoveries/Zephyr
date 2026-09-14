using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Controls;

// Tab overflow: once the strip is wider than the pane the tabs scroll horizontally
// instead of being clipped. Chevrons, the mouse wheel, and a full tab-list dropdown
// all reach the tabs that fall off the end (including their close buttons).
public partial class FilePane
{
    private const double TabScrollStep = 120;

    private bool _suppressTabListSelection;

    private void TabScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateTabOverflowChrome();

    private void UpdateTabOverflowChrome()
    {
        if (TabScroll == null) return;

        // ExtentWidth is the full strip; ViewportWidth is what actually fits.
        bool overflow = TabScroll.ExtentWidth > TabScroll.ViewportWidth + 0.5;

        TabListButton.Visibility  = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabScrollLeft.Visibility  = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabScrollRight.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

        TabScrollLeft.IsEnabled  = overflow && TabScroll.HorizontalOffset > 0.5;
        TabScrollRight.IsEnabled = overflow &&
            TabScroll.HorizontalOffset < TabScroll.ScrollableWidth - 0.5;

        TabScrollLeft.Opacity  = TabScrollLeft.IsEnabled  ? 1 : 0.35;
        TabScrollRight.Opacity = TabScrollRight.IsEnabled ? 1 : 0.35;
    }

    private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        => TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - TabScrollStep);

    private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        => TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset + TabScrollStep);

    // The tab bar has no vertical scrolling of its own, so the wheel drives the strip.
    private void TabBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroll == null || TabScroll.ScrollableWidth <= 0) return;
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // Keeps the selected tab on screen after Ctrl+T, Ctrl+Tab, or a restore.
    private void EnsureActiveTabVisible()
    {
        if (TabScroll == null || Pane?.ActiveTab is not { } active) return;

        Dispatcher.InvokeAsync(() =>
        {
            UpdateTabOverflowChrome();
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(active) is FrameworkElement c)
                c.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void TabListButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressTabListSelection = true;
        TabListBox.SelectedItem = Pane?.ActiveTab;
        _suppressTabListSelection = false;
        TabListPopup.IsOpen = true;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabListSelection) return;
        if (TabListBox.SelectedItem is not TabViewModel tab || Pane == null) return;

        Pane.SelectTabCommand.Execute(tab);
        TabListPopup.IsOpen = false;
        EnsureActiveTabVisible();
    }
}
