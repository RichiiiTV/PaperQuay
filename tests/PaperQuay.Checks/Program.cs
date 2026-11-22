using NAPS2.Images;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PaperQuay;

if (args is ["--check-duplex-pair", var pairFolder])
{
    using var first = new Bitmap(Path.Combine(pairFolder, "page-000001.png"));
    using var second = new Bitmap(Path.Combine(pairFolder, "page-000002.png"));
    Console.WriteLine($"First side: {ScanImageEvidence.InspectReverse(second, first, detectShowThrough: true)}");
    Console.WriteLine($"Second side: {ScanImageEvidence.InspectReverse(first, second, detectShowThrough: true)}");
    return;
}
if (args is ["--check-retained-batch", var retainedBatch])
{
    await ScanImageChecks.CheckRetainedBatch(retainedBatch);
    return;
}
if (args is ["--live-quality", var localFolder])
{
    await ScanImageChecks.CaptureLocal(localFolder);
    return;
}
if (args is ["--replay-quality", var capturedFolder, var replayFolder])
{
    await ScanImageChecks.ReplayLocal(capturedFolder, replayFolder);
    return;
}
if (args is ["--image-evidence", var imageWorkspace])
{
    await ScanImageChecks.Run(imageWorkspace);
    return;
}
if (args is ["--compress-scan", var sourcePath, var destinationPath, var imageFolder])
{
    using var input = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
    using var compressed = new PdfDocument();
    for (var index = 0; index < input.PageCount; index++)
    {
        ScanPdf.CompressPageImage(input.Pages[index], Path.Combine(imageFolder, $"page-{index + 1}.png"));
        compressed.AddPage(input.Pages[index]);
    }
    using var stream = new FileStream(destinationPath, FileMode.CreateNew);
    compressed.Save(stream, false);
    return;
}
if (args is ["--driver", var route, var operation])
{
    await DriverCheck.Run(route, operation);
    return;
}
if (args is ["--paper-sizes"])
{
    DriverCheck.VerifyPaperSizes();
    return;
}
if (args is ["--feeder-recovery", var feederFolder])
{
    await DriverCheck.VerifyFeederRecovery(feederFolder);
    return;
}
if (args is ["--live-queue-click", var clickFolder])
{
    await DriverCheck.VerifyColdQueueClick(clickFolder);
    return;
}
if (args.Length == 2 && args[0] is "--perf" or "--perf-live")
{
    await PerformanceCheck.Run(args[1], live: args[0] == "--perf-live");
    return;
}
if (args is ["--perf-stack", var stackFolder, var mode])
{
    await PerformanceCheck.Run(stackFolder, live: mode == "live", sheets: 10);
    return;
}
if (args is ["--queue", var workspace, var batchFixture, var reviewFixture])
{
    await QueueChecks.Run(workspace, batchFixture, reviewFixture);
    return;
}
if (args is ["--check-queue-junction", var junctionQueue])
{
    using var queue = new FilingQueue(junctionQueue);
    if (queue.Batches.Any() || queue.RecoveryMessage is null)
        throw new InvalidOperationException("A redirected queue batch was accepted.");
    return;
}
if (args is ["--queue-preview", var queueFolder])
{
    var desktop = new Thread(() =>
    {
        using var queue = new FilingQueue(queueFolder);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Workbench(queue, null));
    });
    desktop.SetApartmentState(ApartmentState.STA);
    desktop.Start();
    desktop.Join();
    return;
}
if (args is ["--review-ui", var reviewQueue])
{
    await ReviewChecks.Run(reviewQueue);
    return;
}

using var engine = new DocumentEngine();
if (args is ["--orientation", var orientationFolder])
{
    Directory.CreateDirectory(orientationFolder);
    using var sample = new Bitmap(1900, 2500, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    sample.SetResolution(400, 400);
    using (var drawing = Graphics.FromImage(sample))
    using (var headline = new Font("Arial", 96, FontStyle.Bold, GraphicsUnit.Pixel))
    using (var body = new Font("Arial", 48, FontStyle.Regular, GraphicsUnit.Pixel))
    {
        drawing.Clear(Color.FromArgb(243, 241, 235));
        drawing.DrawString("Kurzbeleg", headline, Brushes.Black, 210, 235);
        drawing.DrawString("Prüfnummer 457821", body, Brushes.Black, 210, 470);
        drawing.DrawString("Original bitte aufbewahren.", body, Brushes.Black, 210, 562);
    }
    foreach (var (angle, scanRotation) in new[] { (0, RotateFlipType.RotateNoneFlipNone),
                 (90, RotateFlipType.Rotate270FlipNone), (180, RotateFlipType.Rotate180FlipNone),
                 (270, RotateFlipType.Rotate90FlipNone) })
    {
        var path = Path.Combine(orientationFolder, $"sparse-{angle}.png");
        using var scan = (Bitmap)sample.Clone();
        scan.RotateFlip(scanRotation);
        scan.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        await foreach (var original in engine.Import(path))
        {
            using (original)
            using (var page = await engine.Prepare(original, null, new(true, true, "deu"), CancellationToken.None))
            {
                Console.WriteLine($"Sparse page needs {angle}°, correction {page.Orientation}°, OCR {page.Quality}");
                if (page.Orientation != angle || !page.Text.Contains("Prüfnummer 457821"))
                    throw new Exception("A sparse page did not yield upright searchable text.");
            }
        }
    }
    return;
}
if (args is ["--devices"])
{
    foreach (var device in await engine.Devices())
        Console.WriteLine(device.Name);
    return;
}
if (args is ["--quality", var qualityPath, var qualityOutput])
{
    var images = new List<ProcessedImage>();
    await foreach (var page in engine.Import(qualityPath)) images.Add(page);
    var results = new PreparedPage?[images.Count];
    try
    {
        var options = new ProcessingOptions(true, true, "deu+eng");
        await Parallel.ForEachAsync(Enumerable.Range(0, images.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (index, token) =>
            {
                results[index] = await engine.Prepare(images[index], null, options, token);
            });
        for (var i = 0; i < results.Length; i++)
        {
            var p = results[i]!;
            Console.WriteLine($"Page {i + 1}, auto angle {p.Orientation}, {p.Quality}, blank: {p.Clues.Blank}, review: {p.NeedsReview}");
        }
        if (results[0]!.Orientation != 90 || !results[0]!.Text.Contains("Prüfnummer 457821"))
            throw new Exception("Sideways German page did not recover upright searchable OCR.");
        if (results[1]!.Orientation != 180 || !results[1]!.Text.Contains("Reference 673942"))
            throw new Exception("Upside-down English page did not recover upright searchable OCR.");
        if (!results[2]!.Text.Contains("907641") || results[2]!.Orientation != 0)
            throw new Exception("The faint document number was lost.");
        if (!results[3]!.Clues.Blank || results[3]!.NeedsReview || results[3]!.Orientation != 0)
            throw new Exception("The blank page was incorrectly rotated or flagged.");
        if (results[4]!.Clues.Blank || !results[4]!.NeedsReview || results[4]!.Orientation != 0)
            throw new Exception("A marked page with no usable OCR should be kept and retain its orientation.");
        using (var manual = await engine.Prepare(images[0], 0, options, CancellationToken.None))
        {
            if (manual.Orientation != 0)
                throw new Exception("Automatic rotation overrode a manual decision.");
        }
        DocumentEngine.Merge(qualityOutput, results.Select(page => page!));
        return;
    }
    finally
    {
        foreach (var page in results) page?.Dispose();
        foreach (var image in images) image.Dispose();
    }
}
if (args is ["--batch", var batchPath, var outDir])
{
    var images = new List<ProcessedImage>();
    var batchOptions = new ProcessingOptions(true, true, "deu");
    await foreach (var page in engine.Import(batchPath)) images.Add(page);
    var results = new PreparedPage?[images.Count];
    try
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, images.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (index, token) =>
            {
                results[index] = await engine.Prepare(images[index], 0, batchOptions, token);
            });
        var input = results.Select((result, index) => new GroupInput(
            result!.Clues, index % 2 == 0 ? SheetSide.Front : SheetSide.Back, index / 2)).ToArray();
        var grouped = DocumentGroups.Plan(input);
        for (var i = 0; i < grouped.Length; i++)
            Console.WriteLine($"{i + 1}: {input[i].Clues} → {grouped[i].Number} {grouped[i].Reason}");
        if (!grouped.Select(item => item.Number).SequenceEqual(new[] { 1, 1, 1, 1, 2, 2, 2, 2, 3 }))
            throw new Exception("Grouping did not match the original documents after real OCR.");
        Directory.CreateDirectory(outDir);
        var sets = results.Select((page, index) => (Page: page!, grouped[index].Number))
            .GroupBy(entry => entry.Number).Select(group => group.Select(item => item.Page).ToArray()).ToArray();
        await QueueChecks.ExportFixture(outDir, sets);
        var quickPages = results.Select(page => page!).ToArray();
        FilingPlan Plan(PreparedPage[] pages, QuickFilingOptions settings)
        {
            var kept = pages.Where(page => !settings.Omits(page)).ToArray();
            return QuickFiling.Plan(kept, pages.Length - kept.Length, settings.DetectByContent, 1);
        }
        var quick = sets.Select(set => Plan(set, new(DetectDocuments: true))).ToArray();
        var quickDocuments = quick.SelectMany(plan => plan.Documents).ToArray();
        if (quick.Any(plan => plan.ReviewReason is not null) || quick.Sum(plan => plan.OmittedBacks) != 5 ||
            !quickDocuments.Select(document => document.Length).SequenceEqual(new[] { 2, 1, 1 }))
            throw new Exception("Automatic filing did not keep the document order and omit both sides of the blank separator.");
        var keepAll = Plan(quickPages, new(SkipBlankBacks: false));
        if (keepAll.ReviewReason is not null || keepAll.Documents.Length != 1 || keepAll.Documents[0].Length != images.Count)
            throw new Exception("One PDF per load did not keep the entire batch.");
        var incomplete = Plan([quickPages[0], quickPages[1]], new(DetectDocuments: true));
        if (incomplete.ReviewReason is null || incomplete.Documents.Length != 0)
            throw new Exception("An incomplete numbered document was filed automatically.");
        var weak = quickPages[0] with { Quality = new OcrQuality(13, 55) };
        var weakPlan = Plan([weak], new());
        if (weakPlan.ReviewReason is not null || weakPlan.WeakOcrPages != 1 ||
            weakPlan.Documents.Single().Single() != weak)
            throw new Exception("Weak OCR blocked filing or lost the page or warning.");
        var unnumberedPages = quickPages.Where(page => !page.Clues.Blank).Take(2)
            .Select(page => page with { Clues = page.Clues with { Number = null, Total = null } }).ToArray();
        var unnumberedPlan = Plan(unnumberedPages, new(DetectDocuments: true));
        if (unnumberedPlan.ReviewReason is not null || unnumberedPlan.Documents.Single().Length != 2)
            throw new Exception("An ordinary unnumbered document was stopped or split.");
        var entirelyBlank = Plan([quickPages[1]], new());
        if (entirelyBlank is not { ReviewReason: null, Documents.Length: 0, OmittedBacks: 1 } ||
            Plan([], new()).ReviewReason is null)
            throw new Exception("Quick filing did not distinguish skipped blank pages from a missing capture.");
        var quickDir = Path.Combine(outDir, "quick");
        Directory.CreateDirectory(quickDir);
        await QueueChecks.ExportFixture(quickDir, quickDocuments);
        Console.WriteLine("Verified quick filing, empty-page removal, and review stops.");
        return;
    }
    finally
    {
        foreach (var page in results) page?.Dispose();
        foreach (var image in images) image.Dispose();
    }
}

using (var photo = new Bitmap(500, 500))
{
    using var graphics = Graphics.FromImage(photo);
    graphics.Clear(Color.DarkBlue);
    var evidence = new PaperCorrection(photo);
    if (evidence.IsPaper || evidence.LooksBlank)
        throw new Exception("A dense color image must not be treated as blank paper.");
}
using (var faint = new Bitmap(500, 500))
{
    using var graphics = Graphics.FromImage(faint);
    graphics.Clear(Color.FromArgb(230, 230, 230));
    using var pen = new Pen(Color.FromArgb(195, 195, 195), 2);
    graphics.DrawLine(pen, 140, 255, 290, 255);
    if (new PaperCorrection(faint).LooksBlank)
        throw new Exception("A faint mark must keep a page out of the blank category.");
}
using (var blankBack = new Bitmap(500, 500, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
{
    using (var graphics = Graphics.FromImage(blankBack)) graphics.Clear(Color.FromArgb(250, 250, 250));
    if (!PaperCorrection.IsUniformPale(blankBack))
        throw new Exception("A uniformly pale back was not eligible for the blank shortcut.");
    blankBack.SetPixel(499, 499, Color.FromArgb(240, 240, 240));
    if (PaperCorrection.IsUniformPale(blankBack))
        throw new Exception("The full-page blank check overlooked a faint edge mark.");
}

var an = PageClues.FromText("RECHNUNG\nRechnungsnummer: INV-20261\nSeite 1 von 2", false);
var continuation = PageClues.FromText("RECHNUNG\nRechnungsnummer: INV-20261\nSeite 2 von 2", false);
var bn = PageClues.FromText("INVOICE\nInvoice no. INV-20262\nPage 1 of 1", false);
var unnumbered = PageClues.FromText("Delivery note", false);
var blank = PageClues.FromText("", true);
var plans = DocumentGroups.Plan([
    new(an, SheetSide.Front, 1),
    new(blank, SheetSide.Back, 1),
    new(continuation, SheetSide.Front, 2),
    new(blank, SheetSide.Back, 2),
    new(bn, SheetSide.Front, 3),
    new(blank, SheetSide.Back, 3),
    new(unnumbered, SheetSide.Front, 4),
    new(blank, SheetSide.Front, 5),
    new(blank, SheetSide.Back, 5),
    new(unnumbered, SheetSide.Front, 6),
    new(bn, SheetSide.Front, 7, false),
    new(continuation, SheetSide.Back, 7, true)
]);
if (!plans.Select(item => item.Number).SequenceEqual(new[] { 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 4 }))
    throw new Exception("Grouping did not preserve duplex backs, separator sheets, and manual changes.");
if (DocumentGroups.Plan([new(an), new(an)])[1].Starts ||
    DocumentGroups.Plan([new(an), new(continuation)])[1].Starts ||
    !DocumentGroups.Plan([new(an), new(bn with { Number = null, Total = null })])[1].Starts)
    throw new Exception("Repeated references, continuation page, or unnumbered new invoice failed.");

var originals = new List<ProcessedImage>();
var output = args[1];
try
{
    await foreach (var page in engine.Import(args[0]))
        originals.Add(page);
    if (originals.Count != 2)
        throw new Exception("Expected the two-page deskew fixture.");
    foreach (var page in originals)
    {
        using (var original = engine.Preview(page))
        {
            var angle = Deskew.CorrectionAngle(original);
            if (angle < 1.4 || angle > 3)
                throw new Exception($"Expected correction around 2.2 degrees, got {angle}.");
            using var corrected = engine.Correct(page, 0, true, true, out var paper);
            if (paper.LooksBlank)
                throw new Exception("Printed page was mistaken for blank.");
            if (Math.Abs(Deskew.CorrectionAngle(corrected)) > 0.4)
                throw new Exception("Corrected page still appears skewed.");
            var shadow = corrected.GetPixel(corrected.Width / 2, corrected.Height * 9 / 10);
            if (shadow.GetBrightness() < 0.92)
                throw new Exception("Faded paper background was not normalized.");
        }
    }
    var options = new ProcessingOptions(true, true, "deu+eng");
    var prepared = new PreparedPage?[originals.Count];
    try
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, originals.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (index, token) =>
            {
                prepared[index] = await engine.Prepare(originals[index], 0, options, token);
            });
        var allPrepared = prepared.Select(page => page!).ToArray();
        DocumentEngine.Merge(output, allPrepared);
        var bytes = await File.ReadAllBytesAsync(output);
        try
        {
            DocumentEngine.Merge(output, allPrepared);
            throw new Exception("Duplicate destination was accepted.");
        }
        catch (IOException) when (File.Exists(output))
        {
            var retained = await File.ReadAllBytesAsync(output);
            if (!bytes.AsSpan().SequenceEqual(retained))
                throw new Exception("Duplicate export modified the original PDF.");
        }
        var batchDir = Path.Combine(Path.GetDirectoryName(output)!, "batch");
        Directory.CreateDirectory(batchDir);
        await QueueChecks.ExportFixture(batchDir, [[allPrepared[0]], [allPrepared[1]]]);
        if (Directory.GetFiles(batchDir, "*.pdf").Length != 2 || Directory.GetFiles(batchDir, "*.tmp").Length != 0)
            throw new Exception("The batch did not publish exactly two PDFs.");
    }
    finally
    {
        foreach (var page in prepared) page?.Dispose();
    }
    Console.WriteLine("Verified adaptive cleanup, grouping, two parallel OCR workers, ordered export, and overwrite protection.");
}
finally
{
    foreach (var page in originals)
        page.Dispose();
}
