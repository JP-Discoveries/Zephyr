using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Dialogs;

/// <summary>
/// VS Code-style command palette: a borderless overlay with a search box and a fuzzy-filtered
/// result list. The chosen item's action is invoked after the window closes so it runs against
/// the restored owner window.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly List<PaletteItem> _all;
    private bool _closing;
    public ObservableCollection<PaletteItem> Results { get; } = [];

    public CommandPaletteWindow(IEnumerable<PaletteItem> items)
    {
        InitializeComponent();
        _all = items.ToList();
        ResultsList.ItemsSource = Results;
        Filter("");

        Loaded += (_, _) => { SearchBox.Focus(); };
        // Close when the palette loses focus, mirroring native command palettes.
        Deactivated += (_, _) => CloseSafe();
    }

    // Close is reachable from Enter, Escape and Deactivated; guard against the re-entrant
    // call WPF makes when closing itself triggers a deactivate.
    private void CloseSafe()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>Centers the palette horizontally over the owner, anchored near the top.</summary>
    public void PositionOver(Window owner)
    {
        double left, top, width, height;
        if (owner.WindowState == WindowState.Maximized)
        {
            var wa = SystemParameters.WorkArea;
            (left, top, width, height) = (wa.Left, wa.Top, wa.Width, wa.Height);
        }
        else
        {
            (left, top, width, height) = (owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        }
        Left = left + (width - Width) / 2;
        Top  = top  + Math.Max(60, height * 0.12);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        Filter(SearchBox.Text);
    }

    private void Filter(string query)
    {
        Results.Clear();
        IEnumerable<PaletteItem> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            matches = _all; // preserve build order (commands first, then navigation)
        }
        else
        {
            matches = _all
                .Select(item => (item, score: Best(query, item)))
                .Where(x => x.score is not null)
                .OrderByDescending(x => x.score!.Value)
                .ThenBy(x => x.item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.item);
        }
        foreach (var m in matches) Results.Add(m);
        if (Results.Count > 0) ResultsList.SelectedIndex = 0;
    }

    // Title matches outrank subtitle (path) matches.
    private static int? Best(string query, PaletteItem item)
    {
        var title = FuzzyMatcher.Score(query, item.Title);
        var sub   = FuzzyMatcher.Score(query, item.Subtitle);
        if (title is null && sub is null) return null;
        return Math.Max((title ?? 0) + 5, sub ?? 0);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up:   Move(-1); e.Handled = true; break;
            case Key.Enter:   Accept();    e.Handled = true; break;
            case Key.Escape:  CloseSafe(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = ResultsList.SelectedIndex + delta;
        ResultsList.SelectedIndex = Math.Clamp(i, 0, Results.Count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ResultsList_Click(object sender, MouseButtonEventArgs e)
    {
        // Execute only when the click lands on an actual item.
        if (ItemFromEvent(e.OriginalSource) is { } item) { ResultsList.SelectedItem = item; Accept(); }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private static PaletteItem? ItemFromEvent(object source)
    {
        var dep = source as System.Windows.DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        return (dep as ListBoxItem)?.DataContext as PaletteItem;
    }

    private void Accept()
    {
        if (ResultsList.SelectedItem is not PaletteItem item) return;
        // Defer the action until after the window closes so it runs against the owner.
        Dispatcher.BeginInvoke(item.Action);
        CloseSafe();
    }
}
