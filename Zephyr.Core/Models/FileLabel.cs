namespace Zephyr.Core.Models;

/// <summary>A named colour label that can be assigned to files and folders.</summary>
public readonly record struct FileLabel(string Key, string Name, string Hex);

public static class FileLabels
{
    /// <summary>The fixed palette of colour labels, in display order.</summary>
    public static readonly IReadOnlyList<FileLabel> All =
    [
        new("red",    "Red",    "#E81123"),
        new("orange", "Orange", "#F7630C"),
        new("yellow", "Yellow", "#F2C811"),
        new("green",  "Green",  "#16C60C"),
        new("blue",   "Blue",   "#0078D7"),
        new("purple", "Purple", "#8764B8"),
        new("gray",   "Gray",   "#8A8A8A"),
    ];

    /// <summary>Resolves a label key to its hex colour, or null if the key is unknown/empty.</summary>
    public static string? HexFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var l in All)
            if (l.Key == key) return l.Hex;
        return null;
    }

    public static string? NameFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var l in All)
            if (l.Key == key) return l.Name;
        return null;
    }
}
