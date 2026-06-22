using Zephyr.Core.Models;

namespace Zephyr.UI.Services;

public static class ClipboardHighlightService
{
    private static HashSet<string>  _paths  = new(StringComparer.OrdinalIgnoreCase);
    private static ClipboardEffect? _effect;

    public static void Set(IEnumerable<string> paths, ClipboardEffect effect)
    {
        _paths  = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        _effect = effect;
    }

    public static void Clear()
    {
        _paths  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _effect = null;
    }

    public static void Apply(IEnumerable<FileItem> items)
    {
        foreach (var item in items)
        {
            item.ClipboardState = _effect switch
            {
                ClipboardEffect.Cut  when _paths.Contains(item.FullPath) => ClipboardItemState.Cut,
                ClipboardEffect.Copy when _paths.Contains(item.FullPath) => ClipboardItemState.Copied,
                _ => ClipboardItemState.None,
            };
        }
    }
}
