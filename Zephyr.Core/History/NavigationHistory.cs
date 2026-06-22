using System.Collections.ObjectModel;

namespace Zephyr.Core.History;

public class NavigationHistory
{
    private const int MaxRecent = 20;

    public ObservableCollection<string> RecentPaths { get; } = [];

    public void Record(string path)
    {
        RecentPaths.Remove(path);
        RecentPaths.Insert(0, path);
        while (RecentPaths.Count > MaxRecent)
            RecentPaths.RemoveAt(RecentPaths.Count - 1);
    }
}
