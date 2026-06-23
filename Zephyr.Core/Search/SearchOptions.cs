namespace Zephyr.Core.Search;

public enum SearchScope    { CurrentDirectory, Recursive }
public enum FileTypeFilter { All, Folders, Documents, Images, Video, Audio, Archives, Code, Executables }
public enum SizeFilter     { All, Tiny, Small, Medium, Large, Huge }
public enum DateFilter     { All, Today, Yesterday, ThisWeek, ThisMonth, ThisYear }

public class SearchOptions
{
    public string      Query          { get; set; } = string.Empty;
    public bool        UseRegex       { get; set; }
    public bool        CaseSensitive  { get; set; }
    public SearchScope Scope          { get; set; } = SearchScope.Recursive;
    public FileTypeFilter TypeFilter  { get; set; } = FileTypeFilter.All;
    public SizeFilter  SizeFilter     { get; set; } = SizeFilter.All;
    public DateFilter  DateFilter     { get; set; } = DateFilter.All;
    public bool        IncludeHidden  { get; set; }
    public bool        IncludeSystem  { get; set; }
    public string      SearchRoot     { get; set; } = string.Empty;
    public string[]?   CustomExtensions { get; set; }

    /// <summary>When true the query is matched against file <i>contents</i> (grep) rather
    /// than names. Folders never match; binary and oversized files are skipped.</summary>
    public bool        MatchContent   { get; set; }
}
