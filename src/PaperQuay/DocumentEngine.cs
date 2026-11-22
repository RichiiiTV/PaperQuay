using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Images.Transforms;
using NAPS2.ImportExport;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using NAPS2.Wia;
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
    private readonly Dictionary<Driver, Task<List<ScanDevice>>> driverProbes = [];

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
        await foreach (var result in DiscoverDevices(CancellationToken.None))
            if (result is not null) devices.AddRange(result);
        return devices;
    }

    public IAsyncEnumerable<List<ScanDevice>?> DiscoverDevices(CancellationToken token) =>
        AwaitDriverProbes([Probe(Driver.Wia), Probe(Driver.Twain)], TimeSpan.FromSeconds(8), token);

    private Task<List<ScanDevice>> Probe(Driver driver)
    {
        if (driverProbes.TryGetValue(driver, out var active) && !active.IsCompleted)
            return active;
        // A timed-out driver keeps its own context and cannot borrow the scanner used for capture.
        return driverProbes[driver] = Task.Run(async () =>
        {
            try
            {
                using var discovery = new DocumentEngine();
                return (await discovery.scanner.GetDeviceList(driver)).ToList();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("{0} scanner discovery failed. {1}", driver, ex.Message);
                return [];
            }
        });
    }

    internal static async IAsyncEnumerable<List<ScanDevice>?> AwaitDriverProbes(
        IEnumerable<Task<List<ScanDevice>>> probes, TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var pending = probes.ToList();
        var deadline = Task.Delay(timeout);
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Cast<Task>().Append(deadline)).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (completed == deadline)
            {
                yield return null;
                yield break;
            }
            var probe = (Task<List<ScanDevice>>)completed;
            pending.Remove(probe);
            yield return await probe;
        }
    }

    public static bool IsCanonWia(ScanDevice device) =>
        device.Driver == Driver.Wia &&
        device.Name.Equals("CANON DR-C225 USB", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<ProcessedImage> Scan(ScanDevice device, PaperSource source, int dpi, ScanPaper paper, BitDepth bitDepth,
        CancellationToken token, IntPtr twainDialogParent = default) =>
        scanner.Scan(CreateScanOptions(device, source, dpi, paper, bitDepth, twainDialogParent), token);

    internal static ScanOptions CreateScanOptions(ScanDevice device, PaperSource source, int dpi, ScanPaper paper,
        BitDepth bitDepth, IntPtr twainDialogParent = default) =>
        new()
        {
            Device = device,
            PaperSource = source,
            PageSize = paper.Size,
            // The DR-C225 feeds narrow sheets between its centered guides.
            PageAlign = source != PaperSource.Flatbed && paper.Size.WidthInMm < PageSize.A4.WidthInMm &&
                device.Name.Contains("DR-C225", StringComparison.OrdinalIgnoreCase)
                ? HorizontalAlign.Center : HorizontalAlign.Right,
            Dpi = dpi,
            BitDepth = bitDepth,
            UseNativeUI = device.Driver == Driver.Twain && twainDialogParent != IntPtr.Zero,
            DialogParent = device.Driver == Driver.Twain ? twainDialogParent : IntPtr.Zero,
            MaxQuality = true,
            ExcludeBlankPages = false
        };

    internal static async IAsyncEnumerable<ProcessedImage> CaptureWithFeederRecovery(
        Func<IAsyncEnumerable<ProcessedImage>> capture, bool duplex, Func<string, int, bool> reload,
        [EnumeratorCancellation] CancellationToken token)
    {
        var sidesPerSheet = duplex ? 2 : 1;
        var received = 0;
        var retrying = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Exception? fault = null;
            string? problem = null;
            var before = received;
            await using (var scan = capture().GetAsyncEnumerator(token))
            {
                while (true)
                {
                    bool next;
                    try { next = await scan.MoveNextAsync(); }
                    catch (Exception ex) when ((problem = FeederProblem(ex, retrying)) is not null)
                    {
                        fault = ex;
                        break;
                    }
                    if (!next) break;
                    received++;
                    yield return scan.Current;
                }
            }
            token.ThrowIfCancellationRequested();
            if (problem is null)
            {
                if (!retrying || received > before) yield break;
                throw new FeederStoppedException("No replacement sheets were captured. Check the pages before filing.");
            }
            if (received % sidesPerSheet != 0)
                throw new FeederStoppedException($"{problem} Only one side of the last sheet was captured. Check that sheet in review.", fault);
            if (!reload(problem, received / sidesPerSheet))
                throw new FeederStoppedException($"{problem} Capture stopped. Check that all pages are present.", fault);
            retrying = true;
        }
    }

    private static string? FeederProblem(Exception error, bool retrying)
    {
        const uint wiaMultiFeed = 0x80210020;
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (unchecked((uint)cause.HResult) == wiaMultiFeed || cause is WiaException { ErrorCode: wiaMultiFeed } ||
                cause is DeviceException { Message: "TWAIN error: PaperDoubleFeed" })
                return "The scanner detected two sheets feeding together.";
            if (cause is DevicePaperJamException)
                return "The scanner reported a paper jam or feed error.";
            if (retrying && cause is DeviceFeederEmptyException)
                return "The scanner did not detect any sheets in the feeder.";
        }
        return null;
    }

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
        CancellationToken token, bool crop = false, bool? verifiedBlank = null)
    {
        var stem = Path.Combine(scratch.FolderPath, Guid.NewGuid().ToString("N"));
        PreparedPage? page = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (verifiedBlank is null)
            {
                using var raw = Preview(original);
                verifiedBlank = ScanImageEvidence.IsEmptyPage(raw);
            }

            ScanInput Render(int angle, string suffix)
            {
                token.ThrowIfCancellationRequested();
                var imagePath = stem + suffix + ".png";
                using var corrected = Correct(original, angle, options.Straighten, options.Clean, out var paper);
                var dpi = Math.Clamp((int)Math.Round(corrected.HorizontalResolution), 70, 9600);
                var bounds = crop ? ScanImageEvidence.SuggestCrop(corrected, out _) : Rectangle.Empty;
                if (bounds.IsEmpty)
                    corrected.Save(imagePath, ImageFormat.Png);
                else
                {
                    using var cropped = corrected.Clone(bounds, PixelFormat.Format24bppRgb);
                    cropped.SetResolution(dpi, dpi);
                    cropped.Save(imagePath, ImageFormat.Png);
                }
                return new ScanInput(imagePath, dpi, angle, paper.IsPaper, paper.LooksBlank,
                    paper.HasDenseGraphics, paper.FaintText, paper.PdfQuality);
            }

            var input = Render(rotation ?? 0, "-original");
            if (verifiedBlank == true)
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
                    CropSuggested: cropSuggested, ReviewCrop: reviewCrop, PdfImageReady: true,
                    WeakWords: best.WeakWords);
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

    private sealed record ScanInput(string Path, int Dpi, int Angle, bool IsPaper, bool LooksBlank,
        bool DenseGraphics, bool FaintText, long PdfQuality);
    private sealed record OcrPass(string Pdf, string Text, OcrQuality Quality, OcrWordRegion[] WeakWords);
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
        var quality = OcrQuality.FromTsv(await File.ReadAllTextAsync(tsv, token), out var weakWords);
        return new OcrPass(pdf, await File.ReadAllTextAsync(text, token), quality, weakWords);
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

internal sealed class FeederStoppedException(string message, Exception? cause = null) : Exception(message, cause);

internal sealed record ProcessingOptions(bool Straighten, bool Clean, string Language, bool AutoOrient = true);

internal sealed record PreparedPage(string Image, string Pdf, PageClues Clues,
    string Text, OcrQuality Quality, int Orientation, bool IsPaper, bool OcrUnavailable = false,
    bool VerifiedBlank = false, bool OmitSide = false, bool CropSuggested = false, bool ReviewCrop = false,
    bool OwnsFiles = true, bool PdfImageReady = false, OcrWordRegion[]? WeakWords = null,
    bool ReviewSide = false) : IDisposable
{
    public static PreparedPage Omitted(ReverseEvidence evidence) => new("", "", PageClues.FromText("", true),
        "", default, 0, true, VerifiedBlank: evidence == ReverseEvidence.Empty, OmitSide: true);

    public bool NeedsReview => OcrUnavailable || (IsPaper && !Clues.Blank && Quality.NeedsReview);
    public bool TrustsPageNumber => !OcrUnavailable && Quality.Confidence >= 80;
    public void Dispose()
    {
        if (!OwnsFiles) return;
        DocumentEngine.DeleteScratchFile(Image);
        DocumentEngine.DeleteScratchFile(Pdf);
    }
}
