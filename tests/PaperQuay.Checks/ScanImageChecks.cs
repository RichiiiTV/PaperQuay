using System.Diagnostics;
using System.Drawing.Imaging;
using System.Threading.Channels;
using NAPS2.Scan;
using PdfSharpCore.Pdf.IO;
using PaperQuay;

internal static class ScanImageChecks
{
    public static async Task Run(string folder)
    {
        var workspace = Directory.CreateDirectory(folder).FullName;
        VerifyPdfPixelScale(workspace);
        VerifyPaperProfiles();
        using var front = new Bitmap(1240, 1754, PixelFormat.Format24bppRgb);
        front.SetResolution(150, 150);
        using (var drawing = Graphics.FromImage(front))
        using (var lettering = new Font("Arial", 35, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            drawing.Clear(Color.FromArgb(250, 250, 250));
            drawing.FillRectangle(Brushes.Gold, 12, 15, 512, 330);
            drawing.FillRectangle(Brushes.Black, 20, 23, 496, 314);
            drawing.FillRectangle(Brushes.White, 37, 40, 462, 280);
            drawing.DrawString("Prüfnummer 457821", lettering, Brushes.Black, 60, 85);
            drawing.DrawString("Original bitte", lettering, Brushes.Black, 60, 142);
            drawing.DrawString("aufbewahren.", lettering, Brushes.Black, 60, 197);
            drawing.FillRectangle(Brushes.Gold, 75, 261, 110, 30);
        }
        using var mirrored = (Bitmap)front.Clone();
        mirrored.RotateFlip(RotateFlipType.RotateNoneFlipX);
        using var ghost = new Bitmap(front.Width, front.Height, PixelFormat.Format24bppRgb);
        ghost.SetResolution(150, 150);
        using (var drawing = Graphics.FromImage(ghost))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix { Matrix00 = .13f, Matrix11 = .13f, Matrix22 = .13f,
                Matrix40 = .87f, Matrix41 = .87f, Matrix42 = .87f });
            drawing.DrawImage(mirrored, new Rectangle(0, 0, ghost.Width, ghost.Height), 0, 0,
                ghost.Width, ghost.Height, GraphicsUnit.Pixel, attributes);
        }
        Require(ScanImageEvidence.IsShowThrough(front, ghost), "A faint mirror of the front was kept as a separate document side.");
        var registeredSpot = new Rectangle(front.Width - 132, 272, 8, 7);
        var faintGold = Color.FromArgb(254, 242, 190);
        using var coloredGhost = WithSpot(ghost, registeredSpot, faintGold);
        Require(ScanImageEvidence.IsShowThrough(front, coloredGhost),
            "A light colored spot aligned with the same stronger color on the front blocked ghost detection.");
        using var differentHue = WithSpot(ghost, registeredSpot, Color.FromArgb(184, 239, 251));
        Require(!ScanImageEvidence.IsShowThrough(front, differentHue),
            "A real differently colored mark was discarded at the same location.");
        using var displacedColor = WithSpot(ghost, new Rectangle(19, 680, 8, 7), faintGold);
        Require(!ScanImageEvidence.IsShowThrough(front, displacedColor),
            "A displaced mark of the same hue was discarded.");
        using var darkMark = WithSpot(ghost, registeredSpot, Color.FromArgb(84, 67, 0));
        Require(!ScanImageEvidence.IsShowThrough(front, darkMark),
            "Dark independent ink was discarded even though its hue matched the front.");
        using var marked = (Bitmap)ghost.Clone();
        using (var drawing = Graphics.FromImage(marked))
        using (var pencil = new Pen(Color.FromArgb(223, 223, 223), 2))
            drawing.DrawLine(pencil, 19, 735, 19, 800);
        Require(!ScanImageEvidence.IsShowThrough(front, marked), "Faint independent writing was discarded as show-through.");
        var fringe = new Rectangle(front.Width - 13, 4, 13, 4);
        var gray = Color.FromArgb(139, 136, 143);
        using var edgeShadow = WithSpot(coloredGhost, fringe, gray);
        Require(ScanImageEvidence.IsShowThrough(front, edgeShadow), "An edge-attached scanner fringe kept a mirrored back.");
        using var detachedMark = WithSpot(coloredGhost, fringe with { X = fringe.X - 5 }, gray);
        Require(!ScanImageEvidence.IsShowThrough(front, detachedMark), "An independent mark near the edge was omitted.");
        using var edgeInk = WithSpot(coloredGhost, fringe, Color.FromArgb(80, 80, 80));
        Require(!ScanImageEvidence.IsShowThrough(front, edgeInk), "Dark ink reaching the physical edge was omitted.");
        var bounds = ScanImageEvidence.SuggestCrop(front, out var reviewCrop);
        Require(reviewCrop && bounds.Contains(new Rectangle(12, 15, 512, 330)) && bounds.Width < front.Width / 2,
            "A corner sign did not produce a bounded crop suggestion.");
        using var darkBed = new Bitmap(800, 560, PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(darkBed))
        {
            drawing.Clear(Color.FromArgb(55, 55, 55));
            drawing.FillRectangle(Brushes.WhiteSmoke, 130, 70, 510, 360);
            drawing.FillRectangle(Brushes.DarkBlue, 360, 225, 64, 48);
        }
        var sheet = ScanImageEvidence.SuggestCrop(darkBed, out var reviewSheet);
        Require(reviewSheet && sheet.Contains(new Rectangle(137, 77, 496, 346)) && sheet.Width < 600,
            "Visible paper edges were not recognized.");

        using var queue = new FilingQueue(Path.Combine(workspace, "queue"));
        using var engine = new DocumentEngine();
        var settings = new ProcessingOptions(true, true, "deu");
        var batch = queue.Begin(workspace, true, settings, new(DetectDocuments: true), 2);
        queue.StorePage(batch.Id, 1, front);
        queue.StorePage(batch.Id, 2, coloredGhost);
        queue.FinishCapture(batch.Id);
        await queue.Prepare(batch.Id, CancellationToken.None);
        var document = queue.Get(batch.Id).Documents.Single();
        Require(document.Reason?.StartsWith("Small item", StringComparison.Ordinal) == true &&
            document.ReviewOmissions?.SequenceEqual(new[] { 2 }) == true && document.OmittedBacks == 1,
            $"The sign did not enter crop review with only its front. {document.Reason} Omitted: {document.OmittedBacks}");
        await foreach (var source in engine.Import(queue.Original(batch.Id, 1)))
        {
            using (source)
            using (var result = await engine.Prepare(source, 0, settings, CancellationToken.None, crop: true))
            {
                Require(result.Text.Contains("457821"), "Applying the crop lost the printable reference text.");
                await queue.Approve(batch.Id, document.Number, [[result]], CancellationToken.None);
            }
        }
        await queue.Transfer(batch.Id, CancellationToken.None);
        var completed = queue.Get(batch.Id);
        var filed = completed.Documents.Single().Outputs!.Single().FiledPath!;
        using (var pdf = PdfReader.Open(filed, PdfDocumentOpenMode.Import))
            Require(pdf.PageCount == 1 && pdf.Pages[0].Width.Point < 300,
                "The approved crop did not export a single compact front.");
        Require(queue.CanReprocess(completed) && File.Exists(queue.Original(batch.Id, 2)),
            "The omitted back was not retained for recovery.");

        var incomplete = queue.Begin(workspace, true, settings, new(), 2);
        queue.StorePage(incomplete.Id, 1, front);
        var held = queue.FinishCapture(incomplete.Id);
        Require(held.NeedsReview && held.ScanError is not null && queue.NextPreparation() is null,
            "An incomplete duplex sheet was treated as complete.");

        using var first = new PreparedPage("", "", PageClues.FromText("Unnumbered report", false),
            "", new OcrQuality(23, 93), 0, true);
        using var second = first with { Clues = PageClues.FromText("Separate letter", false) };
        using var blank = first with { Clues = PageClues.FromText("", true), VerifiedBlank = true };
        Require(QueueRouting.Route([first, second], false, new(DetectDocuments: true)).Single()
                is { Plan.ReviewReason: not null, DecisionPage: 2 },
            "An ambiguous mixed stack was accepted automatically.");
        Require(QueueRouting.Route([first, second], false, new()).Single().Plan.Documents.Single().Length == 2,
            "One PDF per load stopped an ordinary unnumbered batch.");
        Require(QueueRouting.Route([first, blank, second], false, new(DetectDocuments: true))
            .Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([1, 1]),
            "A blank separator failed to split unnumbered documents, or was filed itself.");
        var options = new QuickFilingOptions(DetectDocuments: true);
        using var firstNumbered = first with { Clues = PageClues.FromText("Rechnung\nSeite 1 von 3", false) };
        using var weakMiddle = second with { Quality = new OcrQuality(2, 42) };
        using var lastNumbered = first with { Clues = PageClues.FromText("Blatt 3 / 3", false) };
        Require(QueueRouting.Route([firstNumbered, weakMiddle, lastNumbered], false, options).Single().Plan
            is { ReviewReason: null, WeakOcrPages: 1 }, "A reliable page sequence was blocked by a weak middle page.");
        using var letter = first with { Clues = PageClues.FromText("Kontoauszug", false) };
        using var invoice = first with { Clues = PageClues.FromText("Rechnung\n1 von 1", false) };
        Require(QueueRouting.Route([letter, invoice], false, options).Length == 2,
            "A new title with restarted numbering did not split the document.");
        Console.WriteLine("Verified mirrored show-through, faint independent ink, paper edges, crop and OCR, original retention, and safe grouping.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Bitmap WithSpot(Bitmap image, Rectangle position, Color color)
    {
        var copy = (Bitmap)image.Clone();
        using var drawing = Graphics.FromImage(copy);
        using var brush = new SolidBrush(color);
        drawing.FillRectangle(brush, position);
        return copy;
    }

    private static void VerifyPdfPixelScale(string workspace)
    {
        using var highDpi = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        highDpi.SetResolution(300, 300);
        using (var drawing = Graphics.FromImage(highDpi))
        {
            drawing.Clear(Color.White);
            drawing.FillRectangle(Brushes.Black, 704, 504, 64, 64);
        }
        Require(!PaperCorrection.IsUniformPale(highDpi), "An RGB copy overlooked a mark near the page edge.");
        var png = Path.Combine(workspace, "300dpi.png");
        highDpi.Save(png, ImageFormat.Png);
        var path = Path.Combine(workspace, "300dpi.pdf");
        ScanPdf.WriteImageOnly(png, path, 300);
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var images = pdf.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
        var embedded = images.Elements.GetDictionary(images.Elements.Keys.Single())!;
        using var stream = new MemoryStream(embedded.Stream!.Value);
        using var rendered = new Bitmap(stream);
        Require(rendered.Size == highDpi.Size && rendered.GetPixel(732, 532).GetBrightness() < 0.1 &&
            rendered.GetPixel(235, 170).GetBrightness() > 0.9,
            "JPEG export changed where the image appears on the 300 dpi page.");
    }

    private static void VerifyPaperProfiles()
    {
        using var sample = new Bitmap(500, 740, PixelFormat.Format24bppRgb);
        using var ink = new SolidBrush(Color.FromArgb(170, 30, 68));
        using (var drawing = Graphics.FromImage(sample))
        {
            drawing.Clear(Color.FromArgb(239, 229, 211));
            drawing.FillRectangle(ink, 70, 72, 50, 42);
        }
        var paper = new PaperCorrection(sample);
        using var corrected = paper.Apply(sample);
        var stamp = corrected.GetPixel(90, 90);
        var tint = corrected.GetPixel(310, 340);
        Require(paper.PdfQuality > 84 && stamp.R - stamp.G > 120 && tint.B >= 246,
            "Paper whitening faded a colored stamp or retained the background tint.");
    }

    public static async Task CaptureLocal(string folder)
    {
        var workspace = Directory.CreateDirectory(folder).FullName;
        var output = Directory.CreateDirectory(Path.Combine(workspace, "preview")).FullName;
        using var queue = new FilingQueue(Path.Combine(workspace, "queue"));
        using var engine = new DocumentEngine();
        var settings = new ProcessingOptions(true, true, "deu");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var discovery = Stopwatch.StartNew();
        var device = (await engine.Devices().WaitAsync(deadline.Token)).FirstOrDefault(DocumentEngine.IsCanonWia) ??
            throw new InvalidOperationException("The Canon WIA scanner is unavailable.");
        Console.WriteLine($"Selected {device.Name} via WIA in {discovery.Elapsed.TotalSeconds:F2}s.");
        var batch = queue.Begin(output, true, settings, new(), 2);
        var sheets = Channel.CreateUnbounded<int>();
        var warmup = Task.Run(() => queue.PrepareDuringCapture(batch, sheets.Reader.ReadAllAsync(deadline.Token),
            new Progress<int>(), deadline.Token), deadline.Token);
        var watch = Stopwatch.StartNew();
        var count = 0;
        try
        {
            await foreach (var result in engine.Scan(device, PaperSource.Duplex, 300, deadline.Token))
            {
                var received = watch.Elapsed;
                using (result)
                using (var bitmap = engine.Preview(result))
                {
                    queue.StorePage(batch.Id, ++count, bitmap);
                    Console.WriteLine($"Side {count} at {received.TotalSeconds:F2}s · {bitmap.Width} × {bitmap.Height} · {bitmap.HorizontalResolution:F0} dpi.");
                }
                if (count % 2 == 0) sheets.Writer.TryWrite(count - 1);
            }
        }
        finally { sheets.Writer.TryComplete(); }
        var capture = queue.FinishCapture(batch.Id);
        Console.WriteLine($"Captured and stored {capture.Pages} sides in {watch.Elapsed.TotalSeconds:F2}s.");
        watch.Restart();
        await warmup;
        if (capture is not { Pages: 2, Phase: BatchPhase.Queued })
            throw new InvalidOperationException(capture.ScanError ?? "Expected exactly one duplex sheet.");
        await ProcessLocal(queue, engine, batch, settings);
        Console.WriteLine($"Post-capture through local delivery: {watch.Elapsed.TotalSeconds:F2}s.");
    }

    public static async Task ReplayLocal(string capturedFolder, string folder)
    {
        var workspace = Directory.CreateDirectory(folder).FullName;
        using var queue = new FilingQueue(Path.Combine(workspace, "queue"));
        using var engine = new DocumentEngine();
        var settings = new ProcessingOptions(true, true, "deu");
        var batch = queue.Begin(workspace, true, settings, new(), 2);
        for (var side = 1; side <= 2; side++)
        {
            using var image = new Bitmap(Path.Combine(capturedFolder, $"page-{side:D6}.png"));
            queue.StorePage(batch.Id, side, image);
        }
        queue.FinishCapture(batch.Id);
        await ProcessLocal(queue, engine, batch, settings);
    }

    private static async Task ProcessLocal(FilingQueue queue, DocumentEngine engine, QueueBatch batch, ProcessingOptions settings)
    {
        var watch = Stopwatch.StartNew();
        await queue.Prepare(batch.Id, CancellationToken.None);
        var doc = queue.Get(batch.Id).Documents.Single();
        Console.WriteLine($"Prepared in {watch.Elapsed.TotalSeconds:F1}s. Disposition: {doc.Disposition}. Omitted sides: {doc.OmittedBacks}. {doc.Reason}");
        if (doc.Reason?.StartsWith("Small item", StringComparison.Ordinal) == true &&
            doc.ReviewOmissions is [{ } omitted])
        {
            await foreach (var source in engine.Import(queue.Original(batch.Id, 3 - omitted)))
            {
                using (source)
                using (var cropped = await engine.Prepare(source, null, settings, CancellationToken.None, crop: true))
                {
                    Console.WriteLine($"Cropped front rotation: {cropped.Orientation}°. Searchable characters: {cropped.Quality.Characters}.");
                    await queue.Approve(batch.Id, doc.Number, [[cropped]], CancellationToken.None);
                }
            }
        }
        if (queue.Get(batch.Id).HasPending)
        {
            await queue.Transfer(batch.Id, CancellationToken.None);
            Console.WriteLine($"Filed to local test folder. Originals still available: {queue.CanReprocess(queue.Get(batch.Id))}.");
        }
    }
}
