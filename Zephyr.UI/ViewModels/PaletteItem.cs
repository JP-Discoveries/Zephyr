namespace Zephyr.UI.ViewModels;

/// <summary>One entry in the command palette: a command to run or a place to navigate to.</summary>
public sealed class PaletteItem
{
    public string Title    { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph    { get; init; } = "";   // Segoe Fluent Icons codepoint
    public string Category { get; init; } = "";   // e.g. "Command", "Go to", "Recent"
    public string Gesture  { get; init; } = "";   // optional shortcut hint, e.g. "Ctrl+C"
    public bool   Enabled  { get; init; } = true;
    public Action Action   { get; init; } = static () => { };

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool HasGesture  => !string.IsNullOrEmpty(Gesture);
}

/// <summary>
/// Lightweight fuzzy subsequence matcher. Returns a relevance score (higher is better) or
/// null when the query isn't a subsequence of the target. Rewards contiguous runs and matches
/// at word boundaries so "nf" ranks "New Folder" highly.
/// </summary>
public static class FuzzyMatcher
{
    public static int? Score(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (string.IsNullOrEmpty(target))     return null;

        int qi = 0, score = 0, streak = 0, lastIdx = -1;
        for (int ti = 0; ti < target.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(target[ti]) != char.ToLowerInvariant(query[qi])) continue;

            int gain = 1;
            if (ti == lastIdx + 1) gain += ++streak; else streak = 0;
            if (ti == 0 || target[ti - 1] is ' ' or '\\' or '/' or '_' or '-') gain += 3; // word start
            score  += gain;
            lastIdx = ti;
            qi++;
        }
        return qi == query.Length ? score : null;
    }
}
