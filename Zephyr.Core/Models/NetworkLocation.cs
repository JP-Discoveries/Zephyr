namespace Zephyr.Core.Models;

/// <summary>
/// An entry in the sidebar's Network section: a detected cloud-sync folder, a mapped
/// network drive, or a user-pinned UNC path. Only user pins are removable/persisted.
/// </summary>
public class NetworkLocation
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;   // Segoe Fluent Icons glyph
    public bool   IsRemovable { get; set; } = true;
    public string Detail { get; set; } = string.Empty;   // small secondary line (e.g. the path)
}
