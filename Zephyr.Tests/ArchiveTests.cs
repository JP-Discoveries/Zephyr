using System.IO;
using Zephyr.Core.Archives;
using static Zephyr.Core.Archives.ZephyrArchiveService;

namespace Zephyr.Tests;

public class ArchiveTests : IDisposable
{
    private readonly string _work;
    private readonly string _src;

    public ArchiveTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "zephyr_arc_" + Guid.NewGuid().ToString("N")[..8]);
        _src  = Path.Combine(_work, "src");
        var nested = Path.Combine(_src, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_src, "hello.txt"), "Hello Zephyr");
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "Nested file content");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(WriteFormat.Zip,   ".zip")]
    [InlineData(WriteFormat.TarGz, ".tar.gz")]
    [InlineData(WriteFormat.Tar,   ".tar")]
    public async Task Create_then_extract_round_trips_directory_tree(WriteFormat format, string ext)
    {
        var dest = Path.Combine(_work, "out" + ext);
        await CreateAsync(dest, [_src], new CompressOptions(format, Level.Normal));

        Assert.True(File.Exists(dest), $"{ext} archive was not created");
        Assert.True(CanExtract(dest), $"{ext} should be recognized as extractable");

        var outDir = Path.Combine(_work, "ex_" + format);
        await ExtractAsync(dest, outDir);

        Assert.Equal("Hello Zephyr",        File.ReadAllText(Path.Combine(outDir, "src", "hello.txt")));
        Assert.Equal("Nested file content", File.ReadAllText(Path.Combine(outDir, "src", "nested", "deep.txt")));
    }

    [Fact]
    public async Task Gz_round_trips_a_single_file()
    {
        var file = Path.Combine(_src, "hello.txt");
        var dest = Path.Combine(_work, "hello.txt.gz");
        await CreateAsync(dest, [file], new CompressOptions(WriteFormat.Gz, Level.Maximum));
        Assert.True(File.Exists(dest));

        var outDir = Path.Combine(_work, "ex_gz");
        await ExtractAsync(dest, outDir);
        Assert.Equal("Hello Zephyr", File.ReadAllText(Path.Combine(outDir, "hello.txt")));
    }

    [Fact]
    public async Task Progress_is_reported_and_reaches_completion()
    {
        // A reasonably large, compressible payload so progress spans multiple chunks.
        File.WriteAllText(Path.Combine(_src, "payload.txt"), new string('Z', 5_000_000));

        var reports = new List<ArchiveProgress>();
        var dest = Path.Combine(_work, "out.zip");
        await CreateAsync(dest, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest),
            new Progress<ArchiveProgress>(reports.Add));

        // Progress<T> marshals asynchronously; give the captured reports a moment to drain.
        await Task.Delay(50);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.InRange(r.Fraction, 0.0, 1.0));
        Assert.Equal(reports[^1].TotalBytes, reports[^1].ProcessedBytes);
    }

    [Fact]
    public async Task Maximum_compresses_smaller_than_store()
    {
        var big = Path.Combine(_src, "big.txt");
        File.WriteAllText(big, new string('A', 200_000)); // highly compressible

        var stored = Path.Combine(_work, "stored.zip");
        var maxed  = Path.Combine(_work, "maxed.zip");
        await CreateAsync(stored, [big], new CompressOptions(WriteFormat.Zip, Level.Store));
        await CreateAsync(maxed,  [big], new CompressOptions(WriteFormat.Zip, Level.Maximum));

        Assert.True(new FileInfo(maxed).Length < new FileInfo(stored).Length,
            "Maximum level should produce a smaller archive than Store");
    }

    [Fact]
    public async Task TestAsync_reports_all_entries_ok_for_a_good_archive()
    {
        var zip = Path.Combine(_work, "good.zip");
        await CreateAsync(zip, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest));

        var result = await TestAsync(zip);
        Assert.True(result.AllOk);
        Assert.Equal(2, result.Total); // hello.txt + nested/deep.txt
        Assert.Empty(result.FailedEntries);
    }

    [Theory]
    [InlineData(ZipEncryption.Aes256)]
    [InlineData(ZipEncryption.ZipCrypto)]
    public async Task Encrypted_zip_round_trips_with_password(ZipEncryption method)
    {
        var zip = Path.Combine(_work, "secret.zip");
        await CreateAsync(zip, [_src],
            new CompressOptions(WriteFormat.Zip, Level.Normal, Password: "hunter2", Encryption: method));

        Assert.True(IsEncrypted(zip));
        Assert.True(ValidatePassword(zip, "hunter2"));
        Assert.False(ValidatePassword(zip, "wrong-password"));

        var outDir = Path.Combine(_work, "ex_" + method);
        await ExtractAsync(zip, outDir, new ExtractOptions(Password: "hunter2"));
        Assert.Equal("Hello Zephyr",        File.ReadAllText(Path.Combine(outDir, "src", "hello.txt")));
        Assert.Equal("Nested file content", File.ReadAllText(Path.Combine(outDir, "src", "nested", "deep.txt")));
    }

    [Fact]
    public async Task IsEncrypted_is_false_for_a_normal_archive()
    {
        var zip = Path.Combine(_work, "plain.zip");
        await CreateAsync(zip, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest));
        Assert.False(IsEncrypted(zip));
    }

    [Fact]
    public async Task AppendToZip_adds_and_replaces_entries()
    {
        var zip = Path.Combine(_work, "append.zip");
        var a   = Path.Combine(_work, "a.txt");
        File.WriteAllText(a, "original A");
        await CreateAsync(zip, [a], new CompressOptions(WriteFormat.Zip, Level.Fastest));

        // Add a new file and replace the existing one.
        var b = Path.Combine(_work, "b.txt");
        File.WriteAllText(b, "new B");
        File.WriteAllText(a, "updated A");
        await AppendToZipAsync(zip, [a, b]);

        var outDir = Path.Combine(_work, "ex_append");
        await ExtractAsync(zip, outDir);
        Assert.Equal("updated A", File.ReadAllText(Path.Combine(outDir, "a.txt"))); // replaced, not duplicated
        Assert.Equal("new B",     File.ReadAllText(Path.Combine(outDir, "b.txt"))); // added

        // No duplicate "a.txt" entry should remain.
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Single(z.Entries, e => e.FullName == "a.txt");
    }

    [Fact]
    public void ArchivePath_round_trips()
    {
        var p = ArchivePath.Make(@"C:\stuff\foo.zip", "docs/readme.txt");
        Assert.True(ArchivePath.IsArchivePath(p));
        var (archive, inner) = ArchivePath.Parse(p);
        Assert.Equal(@"C:\stuff\foo.zip", archive);
        Assert.Equal("docs/readme.txt", inner);

        var (_, rootInner) = ArchivePath.Parse(ArchivePath.Make(@"C:\stuff\foo.zip"));
        Assert.Equal("", rootInner);
    }

    [Fact]
    public async Task GetChildren_browses_the_tree()
    {
        var zip = Path.Combine(_work, "browse.zip");
        await CreateAsync(zip, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest));

        var root = GetChildren(zip, "");
        Assert.Single(root);
        Assert.Equal("src", Path.GetFileName(root[0].Path));
        Assert.True(root[0].IsDirectory);

        var srcChildren = GetChildren(zip, "src");
        Assert.Contains(srcChildren, c => c.IsDirectory && Path.GetFileName(c.Path) == "nested");
        Assert.Contains(srcChildren, c => !c.IsDirectory && Path.GetFileName(c.Path) == "hello.txt");

        var nested = GetChildren(zip, "src/nested");
        Assert.Single(nested);
        Assert.Equal("deep.txt", Path.GetFileName(nested[0].Path));
        Assert.False(nested[0].IsDirectory);
    }

    [Fact]
    public async Task ExtractEntryToTemp_returns_file_content()
    {
        var zip = Path.Combine(_work, "browse.zip");
        await CreateAsync(zip, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest));

        var temp = ExtractEntryToTemp(zip, "src/hello.txt");
        try { Assert.Equal("Hello Zephyr", File.ReadAllText(temp)); }
        finally { try { Directory.Delete(Path.GetDirectoryName(temp)!, true); } catch { } }
    }

    [Fact]
    public async Task ExtractEntries_extracts_a_selected_folder_relative_to_base()
    {
        var zip = Path.Combine(_work, "browse.zip");
        await CreateAsync(zip, [_src], new CompressOptions(WriteFormat.Zip, Level.Fastest));

        var dest = Path.Combine(_work, "sel");
        // Browsing "src", user selects the "nested" folder.
        await ExtractEntriesAsync(zip, ["src/nested"], baseInner: "src", dest);

        Assert.Equal("Nested file content", File.ReadAllText(Path.Combine(dest, "nested", "deep.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "hello.txt"))); // not selected
    }

    [Theory]
    [InlineData("archive.7z",  true)]
    [InlineData("photo.RAR",   true)]
    [InlineData("data.tar.xz", true)]
    [InlineData("notes.txt",   false)]
    [InlineData("folder",      false)]
    public void CanExtract_recognizes_supported_formats(string name, bool expected)
        => Assert.Equal(expected, CanExtract(name));
}
