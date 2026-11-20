using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Images.Transforms;
using NAPS2.ImportExport;
using NAPS2.Scan;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace PaperQuay;

internal sealed class DocumentEngine : IDisposable
{
    private readonly GdiImageContext imaging = new();
    private readonly ScanningContext context;
    private readonly ScanController scanner;
    private readonly FileStorageManager scratch;
    private readonly TimeSpan ocrBudget;

    public DocumentEngine(TimeSpan? ocrBudget = null)
    {
        this.ocrBudget = ocrBudget ?? TimeSpan.FromSeconds(25);
        scratch = FileStorageManager.CreateFolder(
            Path.Combine(Path.GetTempPath(), $"PaperQuay-{Guid.NewGuid():N}"));
        context = new ScanningContext(imaging)
        {
            FileStorageManager = scratch,
            TempFolderPath = scratch.FolderPath
        };
        context.SetUpWin32Worker();
        scanner = new ScanController(context);
    }

    public async Task<List<ScanDevice>> Devices()
    {
        List<ScanDevice> devices = [];
        try
        {
            devices.AddRange(await scanner.GetDeviceList(Driver.Wia));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("WIA scanner discovery failed. {0}", ex.Message);
        }
        try
        {
            devices.AddRange(await scanner.GetDeviceList(Driver.Twain));
        }
        catch (Exception ex) when (devices.Count > 0)
        {
            Trace.TraceWarning("TWAIN scanner discovery failed. {0}", ex.Message);
        }
        return devices;
    }

    public static bool IsCanonWia(ScanDevice device) =>
        device.Driver == Driver.Wia &&
        device.Name.Equals("CANON DR-C225 USB", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<ProcessedImage> Scan(ScanDevice device, PaperSource source, int dpi,
        CancellationToken token) =>
        scanner.Scan(new ScanOptions
        {
            Device = device,
            PaperSource = source,
            PageSize = PageSize.A4,
            Dpi = dpi,
            BitDepth = BitDepth.Color,
            MaxQuality = true,
            ExcludeBlankPages = false
        }, token);

    public IAsyncEnumerable<ProcessedImage> Import(string path) =>
        new FileImporter(context).Import(path);

    public Bitmap Preview(ProcessedImage page) => imaging.RenderToBitmap(page);

    public Bitmap Correct(ProcessedImage original, int rotation, bool straighten, bool clean,
        out PaperCorrection paper)
    {
        using var oriented = original.WithTransform(new RotationTransform(rotation), false);
        var rendered = Preview(oriented);
        try
        {
            paper = new PaperCorrection(rendered);
            if (straighten && paper.IsPaper && !paper.LooksBlank)
            {
                var correction = Deskew.CorrectionAngle(rendered);
                if (correction != 0)
                {
                    using var adjusted = oriented.WithTransform(new RotationTransform(correction), false);
                    var straightened = Preview(adjusted);
                    rendered.Dispose();
                    rendered = straightened;
                    paper = new PaperCorrection(rendered);
                }
            }
            if (!clean || !paper.IsPaper)
                return rendered;
            var corrected = paper.Apply(rendered);
            rendered.Dispose();
            return corrected;
        }
        catch
        {
            rendered.Dispose();
            throw;
        }
    }

    public async Task<PreparedPage> Prepare(ProcessedImage original, int? rotation, ProcessingOptions options,
        CancellationToken token, bool crop = false)
    {
        var stem = Path.Combine(scratch.FolderPath, Guid.NewGuid().ToString("N"));
        PreparedPage? page = null;
        try
        {
            ScanInput Render(int angle, string suffix)
            {
                token.ThrowIfCancellationRequested();
                var imagePath = stem + suffix + ".png";
                using var corrected = Correct(original, angle, options.Straighten, options.Clean, out var paper);
                var dpi = Math.Clamp((int)Math.Round(corrected.HorizontalResolution), 70, 9600);
                var blank = false;
                if (paper.LooksBlank)
                {
                    using var raw = Preview(original);
                    blank = PaperCorrection.IsUniformPale(raw);
                }
                var bounds = crop ? ScanImageEvidence.SuggestCrop(corrected, out _) : Rectangle.Empty;
                if (bounds.IsEmpty)
                    corrected.Save(imagePath, ImageFormat.Png);
                else
                {
                    using var cropped = corrected.Clone(bounds, PixelFormat.Format24bppRgb);
                    cropped.SetResolution(dpi, dpi);
                    cropped.Save(imagePath, ImageFormat.Png);
                }
                return new ScanInput(imagePath, dpi, angle, paper.IsPaper, blank, paper.LooksBlank,
                    paper.HasDenseGraphics, paper.FaintText, paper.PdfQuality);
            }

            var input = Render(rotation ?? 0, "-original");
            if (input.Blank)
            {
                var blankPdf = stem + "-blank.pdf";
                ScanPdf.WriteImageOnly(input.Path, blankPdf, input.Dpi, input.PdfQuality);
                page = new PreparedPage(input.Path, blankPdf, PageClues.FromText("", true), "", default,
                    input.Angle, input.IsPaper, VerifiedBlank: true, PdfImageReady: true);
                return page;
            }
            OcrPass? best = null;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(ocrBudget);
            var ocrToken = budget.Token;
            try
            {
                if (input.DenseGraphics)
                {
                    var angle = rotation is null && options.AutoOrient
                        ? await ProbeBlockRotation(input, stem, options.Language, ocrToken)
                        : 0;
                    if (angle is { } turn)
                    {
                        var aligned = turn == 0 ? input : Render(turn, "-block");
                        var block = await Recognize(aligned, stem + "-block-ocr", options.Language, 6, false, ocrToken);
                        if (block.Quality.Characters >= 10 && block.Quality.Confidence >= 79)
                            (input, best) = (aligned, block);
                    }
                }
                var blockRecognized = best is not null;
                best ??= await Recognize(input, stem + "-main", options.Language, 3, input.FaintText, ocrToken);
                if (!blockRecognized && rotation is null && options.AutoOrient && (input.IsPaper || best.Quality.Characters >= 10) &&
                    !best.Quality.ClearlyUpright)
                {
                    async Task<(ScanInput Input, OcrPass Ocr)> ReadAt(int angle)
                    {
                        ocrToken.ThrowIfCancellationRequested();
                        var upright = Render(angle, $"-rotation-{angle}");
                        return (upright, await Recognize(upright, stem + $"-ocr-{angle}",
                            options.Language, 3, upright.FaintText, ocrToken));
                    }

                    var direction = await DetectRotation(input, best.Quality, ocrToken);
                    if (direction != 0)
                    {
                        var proposal = await ReadAt(direction);
                        if (proposal.Ocr.Quality.SupportsOrientation(best.Quality))
                            (input, best) = proposal;
                    }
                    if (input.Angle == 0)
                    {
                        var quality = best.Quality;
                        int[] angles = quality.VerticalLines > quality.HorizontalLines ||
                            (quality.WorthRetrying && quality.Characters >= 10 && !input.IsPaper) ? [90, 270] :
                            quality.WorthRetrying && quality.HorizontalLines > 0 ? [180] : [];
                        var proposals = new List<(ScanInput Input, OcrPass Ocr)>();
                        foreach (var angle in angles)
                        {
                            if (angle == direction) continue;
                            var proposal = await ReadAt(angle);
                            if (proposal.Ocr.Quality.SupportsOrientation(quality))
                                proposals.Add(proposal);
                        }
                        if (proposals.Count == 1 || (proposals.Count == 2 &&
                            proposals[0].Ocr.Quality.ImprovesOn(proposals[1].Ocr.Quality)))
                            (input, best) = proposals[0];
                        else if (proposals.Count == 2 && proposals[1].Ocr.Quality.ImprovesOn(proposals[0].Ocr.Quality))
                            (input, best) = proposals[1];
                    }
                }
                if (input.IsPaper && best.Quality.WorthRetrying)
                {
                    var adaptive = await Recognize(input, stem + "-threshold", options.Language, 3, !input.FaintText, ocrToken);
                    if (adaptive.Quality.ImprovesOn(best.Quality))
                        best = adaptive;
                    if (best.Quality.WorthRetrying)
                    {
                        var alternate = await Recognize(input, stem + "-layout", options.Language,
                            best.Quality.Characters < 20 ? 11 : 6, true, ocrToken);
                        if (alternate.Quality.ImprovesOn(best.Quality))
                            best = alternate;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested) { }
            catch (OcrFailure ex) { Debug.WriteLine(ex.Message); }

            token.ThrowIfCancellationRequested();
            var cropSuggested = false;
            var reviewCrop = false;
            if (!crop)
            {
                using var rendered = new Bitmap(input.Path);
                cropSuggested = !ScanImageEvidence.SuggestCrop(rendered, out reviewCrop).IsEmpty;
            }
            if (best is null)
            {
                var fallback = stem + "-image-only.pdf";
                ScanPdf.WriteImageOnly(input.Path, fallback, input.Dpi, input.PdfQuality);
                page = new PreparedPage(input.Path, fallback, PageClues.FromText("", false), "",
                    default, input.Angle, input.IsPaper, OcrUnavailable: true,
                    CropSuggested: cropSuggested, ReviewCrop: reviewCrop, PdfImageReady: true);
            }
            else
            {
                var pdf = best.Pdf;
                using var source = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
                token.ThrowIfCancellationRequested();
                if (source.PageCount == 1 && ScanPdf.CompressPageImage(source.Pages[0], input.Path,
                    cropSuggested || crop ? 92L : input.PdfQuality))
                {
                    using var compressed = new PdfDocument();
                    compressed.AddPage(source.Pages[0]);
                    pdf = stem + "-compressed.pdf";
                    compressed.Save(pdf);
                }
                page = new PreparedPage(input.Path, pdf, PageClues.FromText(best.Text, input.LooksBlank),
                    best.Text, best.Quality, input.Angle, input.IsPaper,
                    CropSuggested: cropSuggested, ReviewCrop: reviewCrop, PdfImageReady: true);
            }
            return page;
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(scratch.FolderPath, Path.GetFileName(stem) + "-*"))
                if (file != page?.Image && file != page?.Pdf)
                    DeleteScratchFile(file);
        }
    }

    private sealed record ScanInput(string Path, int Dpi, int Angle, bool IsPaper, bool Blank, bool LooksBlank,
        bool DenseGraphics, bool FaintText, long PdfQuality);
    private sealed record OcrPass(string Pdf, string Text, OcrQuality Quality);
    private sealed class OcrFailure(string message) : IOException(message);

    private static async Task<int?> ProbeBlockRotation(ScanInput input, string stem, string language,
        CancellationToken token)
    {
        using var source = new Bitmap(input.Path);
        var scale = Math.Min(1, 1530d / Math.Max(source.Width, source.Height));
        using var preview = new Bitmap(Math.Max(1, (int)(source.Width * scale)),
            Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(preview))
        {
            drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
            drawing.DrawImage(source, 0, 0, preview.Width, preview.Height);
        }
        var credible = new List<(int Angle, OcrQuality Quality)>();
        foreach (var angle in new[] { 0, 90, 180, 270 })
        {
            token.ThrowIfCancellationRequested();
            var file = $"{stem}-probe-{angle}";
            preview.Save(file + ".png", ImageFormat.Png);
            var result = await RunTesseract(input with
            {
                Path = file + ".png", Dpi = Math.Max(70, (int)(input.Dpi * scale))
            }, file, language, ["--psm", "6", "-c", "tessedit_create_tsv=1"], token);
            if (result.ExitCode == 0 && File.Exists(file + ".tsv"))
            {
                var quality = OcrQuality.FromTsv(await File.ReadAllTextAsync(file + ".tsv", token));
                if (quality.Characters >= 12 && quality.Confidence >= 80 && quality.HorizontalLines >= 2 &&
                    quality.HorizontalLines > quality.VerticalLines)
                    credible.Add((angle, quality));
            }
            preview.RotateFlip(RotateFlipType.Rotate90FlipNone);
        }
        var ranked = credible.OrderByDescending(item => item.Quality.Confidence).ToArray();
        return ranked.Length == 1 || (ranked.Length > 1 && ranked[0].Quality.ImprovesOn(ranked[1].Quality))
            ? ranked[0].Angle : null;
    }

    private static async Task<int> DetectRotation(ScanInput input, OcrQuality quality, CancellationToken token)
    {
        var result = await RunTesseract(input, "stdout", "osd", ["--psm", "0"], token);
        if (result.ExitCode != 0)
            return 0;
        var rotation = Regex.Match(result.Output, @"(?m)^Rotate:\s*(90|180|270)\s*$");
        var confidence = Regex.Match(result.Output, @"(?m)^Orientation confidence:\s*([\d.]+)\s*$");
        return rotation.Success &&
               double.TryParse(confidence.Groups[1].Value, CultureInfo.InvariantCulture, out var score) &&
               score >= (quality.WorthRetrying ? 7 : 13)
            ? int.Parse(rotation.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    private static async Task<OcrPass> Recognize(ScanInput input, string output, string language, int mode,
        bool adaptive, CancellationToken token)
    {
        var result = await RunTesseract(input, output, language,
        [
            "--psm", mode.ToString(CultureInfo.InvariantCulture),
            "-c", $"thresholding_method={(adaptive ? 2 : 0)}",
            "-c", "thresholding_kfactor=0.16",
            "-c", "tessedit_create_pdf=1", "-c", "tessedit_create_txt=1", "-c", "tessedit_create_tsv=1"
        ], token);
        if (result.ExitCode != 0)
            throw new OcrFailure($"OCR failed with exit code {result.ExitCode}. {result.Error.Trim()}");
        var pdf = output + ".pdf";
        var text = output + ".txt";
        var tsv = output + ".tsv";
        if (!File.Exists(pdf) || !File.Exists(text) || !File.Exists(tsv))
        {
            throw new OcrFailure("Tesseract did not produce a complete searchable page.");
        }
        return new OcrPass(pdf, await File.ReadAllTextAsync(text, token),
            OcrQuality.FromTsv(await File.ReadAllTextAsync(tsv, token)));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunTesseract(ScanInput input,
        string destination, string language, string[] extra, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = AppContext.BaseDirectory;
        var settings = new ProcessStartInfo(Path.Combine(root, "_win64", "tesseract.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { input.Path, destination, "--tessdata-dir", Path.Combine(root, "tessdata"),
            "--dpi", input.Dpi.ToString(CultureInfo.InvariantCulture), "-l", language }.Concat(extra))
            settings.ArgumentList.Add(arg);
        settings.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = Process.Start(settings) ?? throw new IOException("Could not start Tesseract.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    public static void Merge(string destination, IEnumerable<PreparedPage> pages, CancellationToken token = default)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite);
        using var output = new PdfDocument();
        var sources = new List<PdfDocument>();
        try
        {
            foreach (var page in pages)
            {
                token.ThrowIfCancellationRequested();
                var source = PdfReader.Open(page.Pdf, PdfDocumentOpenMode.Import);
                sources.Add(source);
                if (source.PageCount != 1)
                    throw new IOException("The OCR worker returned an invalid page PDF.");
                if (!page.PdfImageReady) ScanPdf.CompressPageImage(source.Pages[0], page.Image);
                output.AddPage(source.Pages[0]);
            }
            token.ThrowIfCancellationRequested();
            output.Info.Creator = "PaperQuay";
            output.Save(file, false);
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }

    public void Dispose()
    {
        context.Dispose();
        try { scratch.Dispose(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void DeleteScratchFile(string path)
    {
        if (path.Length == 0) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record ProcessingOptions(bool Straighten, bool Clean, string Language, bool AutoOrient = true);

internal sealed record PreparedPage(string Image, string Pdf, PageClues Clues,
    string Text, OcrQuality Quality, int Orientation, bool IsPaper, bool OcrUnavailable = false,
    bool VerifiedBlank = false, bool OmitSide = false, bool CropSuggested = false, bool ReviewCrop = false,
    bool OwnsFiles = true, bool PdfImageReady = false) : IDisposable
{
    public bool NeedsReview => OcrUnavailable || (IsPaper && !Clues.Blank && Quality.NeedsReview);
    public void Dispose()
    {
        if (!OwnsFiles) return;
        DocumentEngine.DeleteScratchFile(Image);
        DocumentEngine.DeleteScratchFile(Pdf);
    }
}
