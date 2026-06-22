using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;

namespace Zephyr.UI.Services;

public static class DocumentTextExtractor
{
    private const int MaxLines = 200;

    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".docx"                    => ExtractZipXml(path, "word/document.xml"),
                ".odt"                     => ExtractZipXml(path, "content.xml"),
                ".odp"                     => ExtractZipXml(path, "content.xml"),
                ".ods"                     => ExtractZipXml(path, "content.xml"),
                ".pptx"                    => ExtractPptx(path),
                ".xlsx"                    => ExtractXlsx(path),
                ".epub"                    => ExtractEpub(path),
                ".pdf"                     => ExtractPdf(path),
                ".doc" or ".xls" or ".ppt" => "[Legacy binary format — open the file to view its contents]",
                _                          => "[Unsupported document format]",
            };
        }
        catch (Exception ex) { return $"[Error reading document: {ex.Message}]"; }
    }

    // ── ZIP-based XML formats (.docx, .odt, .odp, .ods) ─────────────────────

    private static string ExtractZipXml(string path, string entryName)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(entryName);
        if (entry == null) return "[Document content not found]";
        using var stream = entry.Open();
        return ReadXmlText(stream);
    }

    private static string ExtractPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        int slide = 1, lines = 0;
        while (lines < MaxLines)
        {
            var entry = zip.GetEntry($"ppt/slides/slide{slide}.xml");
            if (entry == null) break;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"── Slide {slide} ──");
            using var stream = entry.Open();
            var text = ReadXmlText(stream, MaxLines - lines);
            sb.Append(text);
            lines += text.Count(c => c == '\n') + 2;
            slide++;
        }
        return sb.Length > 0 ? sb.ToString() : "[No text content found]";
    }

    private static string ExtractXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var ss = ssEntry.Open();
            sharedStrings = ReadSharedStrings(ss);
        }

        var sb = new StringBuilder();
        int sheet = 1;
        while (sheet <= 5)
        {
            var entry = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml");
            if (entry == null) break;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"── Sheet {sheet} ──");
            using var stream = entry.Open();
            sb.Append(ReadXlsxSheet(stream, sharedStrings));
            sheet++;
        }
        return sb.Length > 0 ? sb.ToString() : "[No text content found]";
    }

    private static string ExtractEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        int lines = 0;

        var htmlEntries = zip.Entries
            .Where(e => e.FullName.EndsWith(".html",  StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".htm",   StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .Take(5);

        foreach (var entry in htmlEntries)
        {
            if (lines >= MaxLines) break;
            using var stream = entry.Open();
            var text = ReadXmlText(stream, MaxLines - lines);
            sb.Append(text);
            lines += text.Count(c => c == '\n');
        }
        return sb.Length > 0 ? sb.ToString() : "[No text content found]";
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    private static string ExtractPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        int lineCount = 0;

        foreach (var page in document.GetPages())
        {
            if (lineCount >= MaxLines) break;
            var lineBuffer = new StringBuilder();
            double? prevY    = null;
            double? prevXEnd = null;

            foreach (var letter in page.Letters)
            {
                double y = Math.Round(letter.Location.Y, 0);
                double x = letter.Location.X;

                if (prevY.HasValue && Math.Abs(y - prevY.Value) > 3)
                {
                    var line = lineBuffer.ToString().TrimEnd();
                    if (!string.IsNullOrEmpty(line)) { sb.AppendLine(line); lineCount++; }
                    lineBuffer.Clear();
                    prevXEnd = null;
                }
                else if (prevXEnd.HasValue && x - prevXEnd.Value > 4)
                {
                    lineBuffer.Append(' ');
                }

                lineBuffer.Append(letter.Value);
                prevY    = y;
                prevXEnd = x + 5;
            }

            var rem = lineBuffer.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(rem)) { sb.AppendLine(rem); lineCount++; }
        }

        return sb.Length > 0 ? sb.ToString() : "[No text content found]";
    }

    // ── XML text extraction (generic, handles docx/odt/pptx/epub/html) ───────

    private static string ReadXmlText(Stream stream, int maxLines = MaxLines)
    {
        var sb         = new StringBuilder();
        var lineBuffer = new StringBuilder();
        int lineCount  = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing            = DtdProcessing.Ignore,
            IgnoreProcessingInstructions = true,
            IgnoreComments           = true,
            XmlResolver              = null,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read() && lineCount < maxLines)
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    var val = reader.Value;
                    if (!string.IsNullOrEmpty(val))
                    {
                        lineBuffer.Append(val);
                        if (lineBuffer.Length > 120)
                        {
                            FlushLine(sb, lineBuffer, ref lineCount);
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    // Break into a new line at common block-level element boundaries
                    var name = reader.LocalName;
                    if (name is "p" or "para" or "body-text" or "h1" or "h2" or "h3" or
                                "h4" or "h5" or "h6" or "li" or "td" or "th" or "tr" or "div")
                    {
                        FlushLine(sb, lineBuffer, ref lineCount);
                    }
                }
            }
            FlushLine(sb, lineBuffer, ref lineCount);
        }
        catch { return "[Cannot parse document content]"; }

        return sb.Length > 0 ? sb.ToString() : "[No text content found]";
    }

    private static void FlushLine(StringBuilder sb, StringBuilder lineBuffer, ref int lineCount)
    {
        var line = lineBuffer.ToString().Trim();
        lineBuffer.Clear();
        if (!string.IsNullOrEmpty(line))
        {
            sb.AppendLine(line);
            lineCount++;
        }
    }

    // ── Excel shared-string table ─────────────────────────────────────────────

    private static List<string> ReadSharedStrings(Stream stream)
    {
        var result   = new List<string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(stream, settings);
        var sb = new StringBuilder();
        bool inT = false;
        while (reader.Read())
        {
            if      (reader.NodeType == XmlNodeType.Element    && reader.LocalName == "si") sb.Clear();
            else if (reader.NodeType == XmlNodeType.Element    && reader.LocalName == "t")  inT = true;
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "t")  inT = false;
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si") result.Add(sb.ToString());
            else if (reader.NodeType == XmlNodeType.Text && inT) sb.Append(reader.Value);
        }
        return result;
    }

    private static string ReadXlsxSheet(Stream stream, List<string> sharedStrings)
    {
        var rows       = new List<string>();
        var currentRow = new List<string>();
        string? cellType = null;
        bool inV       = false;
        var vBuffer    = new StringBuilder();

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row": currentRow.Clear(); break;
                    case "c":   cellType = reader.GetAttribute("t"); inV = false; vBuffer.Clear(); break;
                    case "v":   inV = true; vBuffer.Clear(); break;
                }
            }
            else if (reader.NodeType == XmlNodeType.Text && inV)
            {
                vBuffer.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "v")
                {
                    inV = false;
                    var val = vBuffer.ToString();
                    if (cellType == "s" && int.TryParse(val, out int idx) && idx < sharedStrings.Count)
                        currentRow.Add(sharedStrings[idx]);
                    else if (cellType != "b")
                        currentRow.Add(val);
                }
                else if (reader.LocalName == "row")
                {
                    if (currentRow.Count > 0) rows.Add(string.Join("\t", currentRow));
                    if (rows.Count >= 60) break;
                }
            }
        }
        return rows.Count > 0 ? string.Join(Environment.NewLine, rows) + Environment.NewLine : string.Empty;
    }
}
