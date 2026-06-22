using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace Zephyr.UI.Services;

public enum ClipboardEffect { Copy, Cut }

public static class ClipboardService
{
    // Reliable string-based format that survives .NET 9 clipboard round-trips
    // (MemoryStream serialization via BinaryFormatter is disabled in .NET 8+)
    private const string ZephyrEffectFormat = "ZephyrClipboardEffect";

    public static void SetFiles(IEnumerable<string> paths, ClipboardEffect effect)
    {
        var data = new DataObject();
        var col  = new StringCollection();
        col.AddRange(paths.ToArray());
        data.SetFileDropList(col);
        data.SetData(ZephyrEffectFormat, effect == ClipboardEffect.Cut ? "Cut" : "Copy");
        // Also set the standard shell format for Explorer interoperability
        var effectVal = effect == ClipboardEffect.Cut ? 2 : 5;
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effectVal)));
        Clipboard.SetDataObject(data, copy: true);
    }

    public static (List<string> Paths, ClipboardEffect Effect) GetFiles()
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return ([], ClipboardEffect.Copy);

        var paths  = new List<string>();
        var effect = ClipboardEffect.Copy;

        if (data.GetData(DataFormats.FileDrop) is string[] files)
            paths.AddRange(files);

        // Prefer our own reliable format; fall back to the shell's MemoryStream format
        if (data.GetData(ZephyrEffectFormat) is string effectStr)
        {
            if (effectStr == "Cut") effect = ClipboardEffect.Cut;
        }
        else if (data.GetData("Preferred DropEffect") is MemoryStream ms)
        {
            ms.Position = 0;
            var buf = new byte[4];
            if (ms.Read(buf, 0, 4) == 4 && BitConverter.ToInt32(buf) == 2)
                effect = ClipboardEffect.Cut;
        }

        return (paths, effect);
    }

    public static bool HasFiles() => Clipboard.ContainsFileDropList();

    public static void Clear() => Clipboard.Clear();
}
