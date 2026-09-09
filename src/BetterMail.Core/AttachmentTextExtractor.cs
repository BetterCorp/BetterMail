using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BetterMail.Core;

public sealed class AttachmentTextExtractor(Func<byte[], bool, CancellationToken, Task<DocumentText>>? recognize = null)
{
    public const int MaximumBytes = 25 * 1024 * 1024;
    public const int MaximumCharacters = 200_000;
    public const int MaximumPages = 100;

    public async Task<DocumentText> ExtractAsync(string name, string contentType, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > MaximumBytes) return new("", "None", false, Issue: new("file_too_large", "Text extraction is limited to 25 MiB per attachment."));
        var extension = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            if (extension == ".pdf" || contentType == "application/pdf")
            {
                var native = await Task.Run(() => ReadPdf(bytes, cancellationToken), cancellationToken);
                if (native.Complete || recognize is null) return native;
                var ocr = await recognize(bytes, true, cancellationToken);
                return Limit(native.Text + "\n" + ocr.Text, "PDF text + " + ocr.Method,
                    native.Issue?.Code != "page_limit" && ocr.Complete, ocr.PagesProcessed, native.TotalPages,
                    native.Issue?.Code == "page_limit" ? native.Issue : ocr.Issue);
            }
            if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return recognize is null
                    ? new("", "None", false, Issue: new("ocr_unavailable", "OCR is not configured. Enable an OCR engine and retry indexing.", Retryable: true))
                    : await recognize(bytes, false, cancellationToken);
            if (extension is ".docx" or ".xlsx" or ".pptx" or ".odt")
                return await Task.Run(() => ReadOffice(bytes, extension, cancellationToken), cancellationToken);
            if (extension is ".txt" or ".csv" or ".tsv" or ".json" or ".xml" or ".html" or ".htm" or ".md" || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaximumCharacters + 1];
                var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
                return Limit(new string(buffer, 0, count), "Text", true);
            }
            return new("", "None", false, Issue: new("unsupported_format", "This file type is preserved for download but has no text extractor. Use PDF, images, text, DOCX, XLSX, PPTX or ODT."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new("", "None", false, Issue: new("extraction_failed", $"Could not extract text: {error.Message}. Check whether the file is corrupt or password protected."));
        }
    }

    private static DocumentText ReadPdf(byte[] bytes, CancellationToken cancellationToken)
    {
        using var pdf = PdfDocument.Open(bytes);
        var text = new StringBuilder();
        var needsOcr = false;
        var processed = 0;
        foreach (var page in pdf.GetPages().Take(MaximumPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page);
            needsOcr |= string.IsNullOrWhiteSpace(pageText) || page.NumberOfImages > 0;
            text.AppendLine($"[Page {page.Number}]").AppendLine(pageText);
            processed++;
            if (text.Length > MaximumCharacters) break;
        }
        var complete = processed == pdf.NumberOfPages && !needsOcr;
        return Limit(text.ToString(), "PDF text", complete, processed, pdf.NumberOfPages,
            processed < pdf.NumberOfPages ? new("page_limit", "Only the first 100 pages / 200,000 characters were indexed.") :
            needsOcr ? new("ocr_required", "This PDF contains images or pages without text; OCR is needed for complete coverage.", Retryable: true) : null);
    }

    private static DocumentText ReadOffice(byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 3000) throw new InvalidDataException("Document contains too many ZIP entries.");
        var entries = zip.Entries.Where(entry => extension switch
        {
            ".docx" => entry.FullName == "word/document.xml" || entry.FullName.StartsWith("word/header", StringComparison.Ordinal) || entry.FullName.StartsWith("word/footer", StringComparison.Ordinal),
            ".xlsx" => entry.FullName == "xl/sharedStrings.xml" || entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal),
            ".pptx" => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal),
            _ => entry.FullName == "content.xml"
        }).ToArray();
        if (entries.Length == 0 || entries.Sum(entry => entry.Length) > 20 * 1024 * 1024)
            throw new InvalidDataException("Missing document XML or expanded content exceeds 20 MiB.");
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            var values = extension == ".odt" ? document.DescendantNodes().OfType<XText>().Select(node => node.Value) :
                document.Descendants().Where(element => element.Name.LocalName == "t" ||
                    element.Name.LocalName == "v" && (string?)element.Parent?.Attribute("t") != "s").Select(element => element.Value);
            text.AppendLine(string.Join(' ', values));
            if (text.Length > MaximumCharacters) return Limit(text.ToString(), "Office XML", false);
        }
        return Limit(text.ToString(), "Office XML", true);
    }

    public static DocumentText Limit(string text, string method, bool complete, int pages = 0, int? total = null, EvidenceIssue? issue = null) =>
        text.Length > MaximumCharacters
            ? new(text[..MaximumCharacters], method, false, pages, total, new("text_limit", "Text was truncated at 200,000 characters."))
            : new(text, method, complete, pages, total, issue);
}
