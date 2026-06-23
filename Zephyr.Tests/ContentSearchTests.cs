using System.IO;
using Zephyr.Core.Search;

namespace Zephyr.Tests;

public class ContentSearchTests : IDisposable
{
    private readonly string _root;
    private readonly SearchEngine _engine = new();

    public ContentSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ZephyrSearch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relativePath, string contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    private string WriteBytes(string relativePath, byte[] contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    private async Task<List<string>> Run(SearchOptions options)
    {
        var names = new List<string>();
        await foreach (var item in _engine.SearchAsync(options))
            names.Add(item.Name);
        return names;
    }

    [Fact]
    public async Task ContentSearch_FindsFileContainingText_IgnoringName()
    {
        Write("alpha.txt", "the quick brown fox");
        Write("beta.txt", "nothing relevant here");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "brown",
            MatchContent = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.Contains("alpha.txt", results);
        Assert.DoesNotContain("beta.txt", results);
    }

    [Fact]
    public async Task ContentSearch_DescendsSubfolders()
    {
        Write("sub/deep/notes.md", "secret token = 42");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "token",
            MatchContent = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.Contains("notes.md", results);
    }

    [Fact]
    public async Task ContentSearch_SkipsBinaryFiles()
    {
        // Text "needle" embedded but preceded by a NUL byte → treated as binary, skipped.
        WriteBytes("blob.bin", [0x00, 0x01, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e']);

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "needle",
            MatchContent = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.DoesNotContain("blob.bin", results);
    }

    [Fact]
    public async Task ContentSearch_DoesNotMatchOnNameOnly()
    {
        Write("brown.txt", "unrelated text");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "brown",
            MatchContent = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.DoesNotContain("brown.txt", results);
    }

    [Fact]
    public async Task ContentSearch_CaseInsensitiveByDefault()
    {
        Write("doc.txt", "Hello World");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "hello",
            MatchContent = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.Contains("doc.txt", results);
    }

    [Fact]
    public async Task ContentSearch_Regex_MatchesPattern()
    {
        Write("log.txt", "error code 0x1F at line 12");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = @"0x[0-9A-F]+",
            MatchContent = true,
            UseRegex     = true,
            Scope        = SearchScope.Recursive,
        });

        Assert.Contains("log.txt", results);
    }

    [Fact]
    public async Task NameSearch_StillMatchesNames_WhenContentModeOff()
    {
        Write("report.txt", "body text");

        var results = await Run(new SearchOptions
        {
            SearchRoot   = _root,
            Query        = "report",
            MatchContent = false,
            Scope        = SearchScope.Recursive,
        });

        Assert.Contains("report.txt", results);
    }
}
