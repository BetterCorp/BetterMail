using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using BetterMail.Core;
#if WINDOWS
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
#endif

namespace BetterMail.App;

internal static class EvidenceOcr
{
    public static async Task<DocumentText> RecognizeAsync(byte[] bytes, bool pdf, CancellationToken cancellationToken)
    {
        try
        {
#if WINDOWS
            return await RecognizeWindowsAsync(bytes, pdf, cancellationToken);
#else
            return await RecognizeExternalAsync(bytes, pdf, cancellationToken);
#endif
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new("", "OCR", false, Issue: new("ocr_failed", $"OCR could not finish: {error.Message}", Retryable: true));
        }
    }

#if WINDOWS
    private static async Task<DocumentText> RecognizeWindowsAsync(byte[] bytes, bool pdf, CancellationToken cancellationToken)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return new("", "Windows OCR", false, Issue: new("ocr_language_missing", "Install an OCR language in Windows Settings > Time & language, then retry.", Retryable: true));
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken);
        }
        input.Seek(0);
        if (!pdf)
        {
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
            var text = new StringBuilder();
            var count = Math.Min(decoder.FrameCount, AttachmentTextExtractor.MaximumPages);
            var framesProcessed = 0;
            for (uint index = 0; index < count; index++)
            {
                var frame = await decoder.GetFrameAsync(index).AsTask(cancellationToken);
                text.AppendLine(await ReadFrameAsync(engine, frame, cancellationToken));
                framesProcessed++;
                if (text.Length > AttachmentTextExtractor.MaximumCharacters) break;
            }
            return AttachmentTextExtractor.Limit(text.ToString(), "Windows OCR", framesProcessed == decoder.FrameCount,
                framesProcessed, (int)decoder.FrameCount, framesProcessed < decoder.FrameCount ? new("page_limit", "OCR is limited to 100 image frames / 200,000 characters.") : null);
        }
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input).AsTask(cancellationToken);
        var pages = Math.Min(document.PageCount, AttachmentTextExtractor.MaximumPages);
        var result = new StringBuilder();
        var processed = 0;
        for (uint index = 0; index < pages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage(index);
            using var rendered = new InMemoryRandomAccessStream();
            var scale = Math.Min(2400d / Math.Max(page.Size.Width, page.Size.Height), 3);
            await page.RenderToStreamAsync(rendered, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            }).AsTask(cancellationToken);
            rendered.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(rendered).AsTask(cancellationToken);
            var frame = await decoder.GetFrameAsync(0).AsTask(cancellationToken);
            result.AppendLine($"[OCR page {index + 1}]").AppendLine(await ReadFrameAsync(engine, frame, cancellationToken));
            processed++;
            if (result.Length > AttachmentTextExtractor.MaximumCharacters) break;
        }
        return AttachmentTextExtractor.Limit(result.ToString(), "Windows OCR", processed == document.PageCount,
            processed, (int)document.PageCount, processed < document.PageCount ? new("page_limit", "OCR is limited to 100 pages / 200,000 characters.") : null);
    }

    private static async Task<string> ReadFrameAsync(OcrEngine engine, BitmapFrame frame, CancellationToken cancellationToken)
    {
        var scale = Math.Min(1, 2400d / Math.Max(frame.PixelWidth, frame.PixelHeight));
        using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = (uint)Math.Max(1, frame.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, frame.PixelHeight * scale) },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join('\n', result.Lines.Select(line => line.Text));
    }
#endif

    internal static async Task<DocumentText> RecognizeExternalAsync(byte[] bytes, bool pdf, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("bettermail-ocr-");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var input = Path.Combine(directory.FullName, pdf ? "input.pdf" : "input.image");
            await File.WriteAllBytesAsync(input, bytes, cancellationToken);
            string[] images;
            if (pdf)
            {
                await RunAsync(Environment.GetEnvironmentVariable("BETTERMAIL_PDFTOPPM_PATH") ?? "pdftoppm",
                    ["-f", "1", "-l", "100", "-scale-to", "2400", "-png", input, Path.Combine(directory.FullName, "page")], cancellationToken);
                images = Directory.GetFiles(directory.FullName, "page-*.png").OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            }
            else images = [input];
            var text = new StringBuilder();
            var processed = 0;
            foreach (var image in images.Take(AttachmentTextExtractor.MaximumPages))
            {
                text.AppendLine(await RunAsync(Environment.GetEnvironmentVariable("BETTERMAIL_TESSERACT_PATH") ?? "tesseract",
                    [image, "stdout", "-l", Environment.GetEnvironmentVariable("BETTERMAIL_OCR_LANGUAGE") ?? "eng"], cancellationToken));
                processed++;
                if (text.Length > AttachmentTextExtractor.MaximumCharacters) break;
            }
            return AttachmentTextExtractor.Limit(text.ToString(), "Tesseract OCR", processed > 0 && processed == images.Length && images.Length < 100,
                processed, pdf ? null : 1, images.Length >= 100 ? new("page_limit", "OCR is limited to the first 100 pages.") : null);
        }
        catch (Win32Exception)
        {
            return new("", "Tesseract OCR", false, Issue: new("ocr_unavailable", "Install Tesseract and Poppler (pdftoppm) and put them on PATH, then retry. BETTERMAIL_TESSERACT_PATH and BETTERMAIL_PDFTOPPM_PATH may specify their executables.", Retryable: true));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the OCR process.");
        try
        {
            var output = ReadOutputAsync(process.StandardOutput, timeout.Token);
            var error = ReadOutputAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited with {process.ExitCode}: {error.Result[..Math.Min(error.Result.Length, 2000)]}");
            return output.Result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                throw new EvidenceException("ocr_timeout", "OCR exceeded 90 seconds. Retry with a smaller document.", true);
            throw;
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[AttachmentTextExtractor.MaximumCharacters + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (read > AttachmentTextExtractor.MaximumCharacters) throw new IOException("OCR output exceeds 200,000 characters.");
        return new string(buffer, 0, read);
    }
}
