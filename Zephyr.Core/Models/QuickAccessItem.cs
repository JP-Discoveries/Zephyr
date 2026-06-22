namespace Zephyr.Core.Models;

public class QuickAccessItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Icon { get; init; } = "";
}
