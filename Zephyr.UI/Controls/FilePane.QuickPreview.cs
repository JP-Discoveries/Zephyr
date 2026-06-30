using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Zephyr.Core.Models;
using Zephyr.UI.Services;

namespace Zephyr.UI.Controls;

// Space-bar "Quick Look" overlay (image / text / document / PDF page render) plus
// type-to-jump list navigation. Both live on the list's PreviewKeyDown handler.
public partial class FilePane
{
    // ── Quick Preview (Space bar) ─────────────────────────────────────────

    private bool _quickPreviewVisible;
    private CancellationTokenSource? _quickPreviewCts;

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_quickPreviewVisible) CloseQuickPreview();
            else if (Tab?.SelectedItem is { } item) _ = ShowQuickPreviewAsync(item);
            return;
        }

        if (e.Key == Key.Escape && _quickPreviewVisible)
        {
            e.Handled = true;
            CloseQuickPreview();
            return;
        }

        // While the preview is open, arrow keys move the selection and the preview
        // follows it live (macOS Quick Look style).
        if (_quickPreviewVisible &&
            e.Key is Key.Up or Key.Down or Key.Left or Key.Right &&
            e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            int delta = e.Key is Key.Up or Key.Left ? -1 : +1;
            if (MovePreviewSelection(sender as ListBox, delta) is { } next)
                _ = ShowQuickPreviewAsync(next);
            return;
        }

if (e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            var c = KeyToChar(e.Key);
            if (c.HasValue)
            {
                e.Handled = true;
                JumpToLetter(c.Value, sender as ItemsControl);
            }
        }
    }

    // Moves the active list's selection by delta (clamped) and returns the newly
    // selected item, so the open preview can refresh to match.
    private FileItem? MovePreviewSelection(ListBox? list, int delta)
    {
        var items = Tab?.Items;
        if (list == null || items == null || items.Count == 0) return null;

        int index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
        index = Math.Clamp(index + delta, 0, items.Count - 1);

        var next = items[index];
        list.SelectedItem = next;
        list.ScrollIntoView(next);
        return next;
    }

    private async Task ShowQuickPreviewAsync(FileItem item)
    {
        _quickPreviewCts?.Cancel();
        _quickPreviewCts = new CancellationTokenSource();
        var ct = _quickPreviewCts.Token;

        QuickPreviewTitle.Text             = item.Name;
        QuickPreviewImageScroll.Visibility = Visibility.Collapsed;
        QuickPreviewTextScroll.Visibility  = Visibility.Collapsed;
        QuickPreviewInfo.Visibility        = Visibility.Collapsed;
        QuickPreviewImage.Source           = null;
        QuickPreviewPdfPages.ItemsSource   = null;
        QuickPreviewOverlay.Visibility     = Visibility.Visible;
        _quickPreviewVisible               = true;

        // PDFs render to actual page images rather than scraped text.
        if (!item.IsDirectory && string.Equals(item.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            QuickPreviewText.Text             = "Rendering…";
            QuickPreviewTextScroll.Visibility = Visibility.Visible;
            try
            {
                var path  = item.FullPath;
                var pages = await PdfRenderService.RenderPagesAsync(path, ct);
                if (ct.IsCancellationRequested) return;
                if (pages.Count == 0) { ShowQPInfo(item); return; }
                QuickPreviewTextScroll.Visibility  = Visibility.Collapsed;
                QuickPreviewPdfPages.ItemsSource   = pages;
                QuickPreviewImageScroll.Visibility = Visibility.Visible;
                QuickPreviewImageScroll.ScrollToTop();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { QuickPreviewText.Text = $"[Cannot render PDF: {ex.Message}]"; }
            return;
        }

        var previewType = item.IsDirectory ? PreviewType.Info : PreviewService.GetType(item.Extension);

        switch (previewType)
        {
            case PreviewType.Image:
                try
                {
                    var bmp = await Task.Run(() =>
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.UriSource    = new Uri(item.FullPath);
                        b.CacheOption  = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        return b;
                    }, ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewImage.Source            = bmp;
                    QuickPreviewImageScroll.Visibility  = Visibility.Visible;
                }
                catch (OperationCanceledException) { return; }
                catch { ShowQPInfo(item); }
                break;

            case PreviewType.Text:
                QuickPreviewText.Text              = "Loading…";
                QuickPreviewTextScroll.Visibility  = Visibility.Visible;
                try
                {
                    var text = await Task.Run(() =>
                    {
                        var sb = new StringBuilder();
                        using var reader = new StreamReader(item.FullPath, detectEncodingFromByteOrderMarks: true);
                        for (int i = 0; i < 200 && !reader.EndOfStream; i++)
                            sb.AppendLine(reader.ReadLine());
                        return sb.ToString();
                    }, ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewText.Text = text;
                }
                catch (OperationCanceledException) { return; }
                catch { QuickPreviewText.Text = "[Cannot read file]"; }
                break;

            case PreviewType.Document:
                QuickPreviewText.Text             = "Loading…";
                QuickPreviewTextScroll.Visibility = Visibility.Visible;
                try
                {
                    var path = item.FullPath;
                    var docText = await Task.Run(() => DocumentTextExtractor.Extract(path), ct);
                    if (ct.IsCancellationRequested) return;
                    QuickPreviewText.Text = docText;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { QuickPreviewText.Text = $"[Cannot read document: {ex.Message}]"; }
                break;

            default:
                ShowQPInfo(item);
                break;
        }
    }

    private void ShowQPInfo(FileItem item)
    {
        QuickPreviewInfoIcon.Text           = item.Icon;
        QuickPreviewInfoType.Text           = item.TypeDisplay;
        QuickPreviewInfoSize.Text           = item.IsDirectory ? item.ContentSummary : item.SizeDisplay;
        QuickPreviewInfoDate.Text           = $"Modified  {item.LastModified:yyyy-MM-dd  HH:mm}";
        QuickPreviewInfo.Visibility         = Visibility.Visible;
    }

    private void CloseQuickPreview()
    {
        _quickPreviewCts?.Cancel();
        _quickPreviewVisible               = false;
        QuickPreviewOverlay.Visibility     = Visibility.Collapsed;
        QuickPreviewImage.Source           = null;
        QuickPreviewPdfPages.ItemsSource   = null;
        QuickPreviewText.Text              = string.Empty;
    }

    private void QuickPreviewClose_Click(object sender, RoutedEventArgs e) => CloseQuickPreview();

    private void QuickPreviewOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == QuickPreviewBackdrop)
            CloseQuickPreview();
    }

    private void QuickPreviewCard_MouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    // ── Jump-to-letter ────────────────────────────────────────────────────

    private string   _jumpBuffer   = string.Empty;
    private DateTime _lastJumpTime = DateTime.MinValue;
    private const int JumpTimeoutMs = 700;

    private void JumpToLetter(char c, ItemsControl? list)
    {
        var items = Tab?.Items;
        if (items == null || list == null) return;

        var now = DateTime.UtcNow;
        if ((now - _lastJumpTime).TotalMilliseconds > JumpTimeoutMs)
            _jumpBuffer = string.Empty;
        _lastJumpTime = now;
        _jumpBuffer  += c.ToString();

        var match = items.FirstOrDefault(i =>
            i.Name.StartsWith(_jumpBuffer, StringComparison.OrdinalIgnoreCase));

        // If no match for accumulated buffer, fall back to just the new char
        if (match == null && _jumpBuffer.Length > 1)
        {
            _jumpBuffer = c.ToString();
            match = items.FirstOrDefault(i =>
                i.Name.StartsWith(_jumpBuffer, StringComparison.OrdinalIgnoreCase));
        }

        if (match == null) return;
        if (list is ListView lv) { lv.SelectedItem = match; lv.ScrollIntoView(match); }
        else if (list is ListBox lb) { lb.SelectedItem = match; lb.ScrollIntoView(match); }
    }

    private static char? KeyToChar(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (char)('a' + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
        return null;
    }
}
