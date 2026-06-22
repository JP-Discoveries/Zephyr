namespace Zephyr.UI.Services;

public enum PreviewType { None, Image, Text, Document, Info }

public static class PreviewService
{
    private static readonly HashSet<string> ImageExts = new(
    [".jpg",".jpeg",".png",".gif",".bmp",".webp",".ico",".tiff",".avif"],
    StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextExts = new(
    [".txt",".md",".cs",".vb",".fs",".js",".ts",".jsx",".tsx",".py",".rb",".go",
     ".rs",".java",".cpp",".c",".h",".json",".xml",".xaml",".html",".htm",".css",
     ".scss",".yaml",".yml",".toml",".ini",".cfg",".log",".sh",".bat",".cmd",".ps1",
     ".sql",".gitignore",".env",".editorconfig",".reg",".vbs",".wsf",".nfo",".csv",
     ".tsv",".rtf",".diff",".patch",".tf",".hcl",".proto",".graphql",".gql"],
    StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DocumentExts = new(
    [".docx",".doc",".pdf",".odt",".pptx",".ppt",".xlsx",".xls",".odp",".ods",".epub"],
    StringComparer.OrdinalIgnoreCase);

    public static PreviewType GetType(string extension) =>
        ImageExts.Contains(extension)    ? PreviewType.Image    :
        TextExts.Contains(extension)     ? PreviewType.Text     :
        DocumentExts.Contains(extension) ? PreviewType.Document :
                                           PreviewType.Info;

    public static bool IsImage(string extension) => ImageExts.Contains(extension);
}
