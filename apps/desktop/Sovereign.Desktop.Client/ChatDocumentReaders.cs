using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sovereign.Desktop.Client;

public sealed partial class ChatAttachmentStore
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".mdown", ".rst", ".csv", ".tsv", ".json", ".jsonl",
        ".py", ".cs", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".xml", ".yaml", ".yml",
        ".toml", ".log", ".sql", ".rs", ".go", ".cpp", ".h", ".ps1", ".sh", ".ini", ".cfg",
        ".sha256", ".sha512", ".sha1", ".md5", ".tex"
    };
    private const int MaxArchiveText = 140000;
    private const long MaxExpandedBytes = 20 * 1024 * 1024;
    private static void ValidatePlainText(string value)
    {
        if (value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f')))
            throw new IOException("Binary content is not readable text.");
    }
    // Refresh saved metadata after a reader upgrade, using only the integrity-checked owned copy.
    public async Task<ChatAttachment> RefreshAsync(ChatAttachment item, CancellationToken ct)
    {
        using var lease = new FileStream(PathFor(item), FileMode.Open, FileAccess.Read, FileShare.Read);
        await ReadVerifiedAsync(item, ct);
        if (item.Kind is "image" or "video" or "audio") return item;
        var result = await Task.Run(() => Extract(PathFor(item), item.Name, item.Bytes), ct);
        return item with { Kind = result.Item1, Mime = result.Item2, ExtractedText = result.Item3, Note = result.Item4 };
    }
    private static (string, string, string?, string) ReadDocumentArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 1000) throw new IOException("ZIP has too many entries.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new IOException("ZIP exceeds the 20 MB expanded limit.");
        }
        var text = new StringBuilder(); var read = 0; var skipped = 0; var clipped = false;
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(part => part == "..") || name.Contains(':'))
            { skipped++; continue; }
            if (!TextExtensions.Contains(Path.GetExtension(name))) { skipped++; continue; }
            if (text.Length >= MaxArchiveText) { skipped++; clipped = true; continue; }
            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                var remaining = Math.Min(MaxText, MaxArchiveText - text.Length);
                var chars = new char[remaining + 1]; var n = reader.ReadBlock(chars, 0, chars.Length);
                var value = new string(chars, 0, Math.Min(n, remaining)); ValidatePlainText(value);
                if (string.IsNullOrWhiteSpace(value)) { skipped++; continue; }
                text.AppendLine("[Archive entry: " + name + "]").AppendLine(value);
                if (n > remaining) { text.AppendLine("[ENTRY TEXT TRUNCATED]"); clipped = true; }
                read++;
            }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException or InvalidDataException)
            { skipped++; }
        }
        if (read == 0) return ("file", "application/zip", null, "ZIP has no readable text/source files. Extract DOCX/PDF files and attach them directly. Binary and nested archives are not analyzed.");
        var note = $"ZIP: {read} text/source files read; {skipped} entries omitted. " + (clipped ? "Text truncated. " : "") + "Binary files and nested archives are not analyzed or executed.";
        return ("text", "application/zip", text + "\n[" + note + "]", note);
    }
    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > 8 * 1024 * 1024) throw new IOException("Office XML is too large.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static (string, string, string?, string) ReadOfficeDocument(string path, string extension)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > MaxExpandedBytes) throw new IOException("Office document exceeds extraction limits.");
        var text = new StringBuilder(); var truncated = false;
        if (extension == ".pptx")
        {
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal))
            {
                text.AppendLine("[" + entry.FullName + "]");
                foreach (var t in ReadXml(entry).Descendants(a + "t")) text.AppendLine(t.Value);
                if (text.Length > MaxText) { truncated = true; break; }
            }
        }
        else
        {
            XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
            var shared = sharedEntry is null ? [] : ReadXml(sharedEntry).Descendants(x + "si").Select(si => string.Concat(si.Descendants(x + "t").Select(t => t.Value))).ToArray();
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal))
            {
                text.AppendLine("[" + entry.FullName + "]");
                foreach (var cell in ReadXml(entry).Descendants(x + "c"))
                {
                    var value = cell.Element(x + "v")?.Value ?? string.Concat(cell.Descendants(x + "t").Select(t => t.Value));
                    if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var index)) value = index >= 0 && index < shared.Length ? shared[index] : "[invalid shared string]";
                    text.Append((string?)cell.Attribute("r")).Append(": ").Append(value);
                    if (cell.Element(x + "f") is { } formula) text.Append(" [formula: ").Append(formula.Value).Append("; cached value, not recalculated]");
                    text.AppendLine();
                    if (text.Length > MaxText) { truncated = true; break; }
                }
                if (truncated) break;
            }
        }
        var output = text.ToString();
        if (string.IsNullOrWhiteSpace(output)) return ("file", "application/octet-stream", null, "No readable Office text found.");
        if (output.Length > MaxText) { output = output[..MaxText] + "\n[EXTRACTION TRUNCATED]"; truncated = true; }
        var note = (truncated ? "Text extracted; truncated. " : "Text extracted. ") + (extension == ".xlsx" ? "Raw cell values; dates may be serial values. Formulas are not recalculated. " : "Slide text only. ") + "Images, charts, macros and embedded objects are not analyzed.";
        return ("text", "text/plain", output, note);
    }
}
