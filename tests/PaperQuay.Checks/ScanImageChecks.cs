using System.Diagnostics;
using System.Drawing.Imaging;
using System.Threading.Channels;
using NAPS2.Images;
using NAPS2.Scan;
using PdfSharpCore.Pdf.IO;
using PaperQuay;

internal static class ScanImageChecks
{
    public static async Task CheckRetainedBatch(string folder)
    {
        var batch = System.Text.Json.JsonSerializer.Deserialize<QueueBatch>(File.ReadAllText(Path.Combine(folder, "batch.json")))!;
        var evidence = new List<ReverseEvidence>();
        for (var number = 1; number <= batch.Pages; number += batch.Duplex ? 2 : 1)
        {
            using var front = new Bitmap(Path.Combine(folder, $"page-{number:D6}.png"));
            using var back = batch.Duplex && number < batch.Pages
                ? new Bitmap(Path.Combine(folder, $"page-{number + 1:D6}.png")) : null;
            evidence.AddRange(ScanImageEvidence.CheckSheet(front, back, batch.Filing));
        }
        Console.WriteLine($"Skipped sides: {string.Join(", ", Enumerable.Range(1, evidence.Count)
            .Where(side => batch.Filing.Omits(evidence[side - 1])))}.");
        var pages = new List<PreparedPage>();
        using var engine = new DocumentEngine();
        try
        {
            for (var side = 1; side <= evidence.Count; side++)
            {
                var classification = evidence[side - 1];
                var prepared = batch.Filing.Omits(classification) ? PreparedPage.Omitted(classification) : await ReadSide(side);
                pages.Add(prepared with { ReviewSide = classification == ReverseEvidence.Ambiguous });
            }
            foreach (var group in QueueRouting.Route(pages.ToArray(), batch.Duplex, batch.Filing))
                Console.WriteLine($"Group {group.Number}: source sides {group.First}–{group.Last}, " +
                    $"skipped {group.Plan.OmittedBacks}, review {group.Plan.ReviewReason is not null}.");
        }
        finally { foreach (var page in pages) page.Dispose(); }

        async Task<PreparedPage> ReadSide(int side)
        {
            var cache = Directory.GetFiles(folder, $"prepared-v*-{side:D6}.json")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (cache is not null)
            {
                using var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cache));
                var stored = System.Text.Json.JsonSerializer.Deserialize<PreparedPage>(data.RootElement.GetProperty("Page"))!;
                if (!stored.OmitSide) return stored with { OwnsFiles = false };
            }
            await foreach (var image in engine.Import(Path.Combine(folder, $"page-{side:D6}.png")))
                using (image)
                    return await engine.Prepare(image, null, batch.Processing, CancellationToken.None, verifiedBlank: false);
            throw new InvalidDataException($"Side {side} could not be read.");
        }
    }
    public static async Task Run(string folder)
    {
        var workspace = Directory.CreateDirectory(folder).FullName;
        VerifyPdfPixelScale(workspace);
        VerifyPaperProfiles();
        var profile = new ScanProfile("Receipts", workspace, "A5", 400, 1, false, 2,
            new ProcessingOptions(true, true, "eng"), new QuickFilingOptions(RemoveBlankPages: false, SheetsPerDocument: 3));
        var restoredProfiles = OutputFolder.ParseProfiles(System.Text.Json.JsonSerializer.Serialize(new[]
        {
            profile, profile with { Name = "receipts" }, profile with { Name = "Invalid", Folder = "relative" }
        }));
        Require(restoredProfiles.SequenceEqual([profile]), "A saved scan profile did not round-trip or allowed duplicates.");
        var quality = OcrQuality.FromTsv(
            "5\t1\t1\t1\t1\t1\t30\t40\t50\t20\t60.0\tTotal\n" +
            "5\t1\t1\t1\t1\t2\t90\t40\t50\t20\t97.0\t1234", out var highlights);
        Require(quality.Characters == 9 && highlights.SequenceEqual([new OcrWordRegion(30, 40, 50, 20)]),
            "OCR review did not keep only the low-confidence word's coordinates.");
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
        queue.StorePage(incomplete.Id, 2, coloredGhost);
        queue.StorePage(incomplete.Id, 3, front);
        var held = queue.FinishCapture(incomplete.Id);
        Require(held.NeedsReview && held.ScanError is not null && queue.NextPreparation() is null,
            "An incomplete duplex sheet was treated as complete.");

        using var shaded = new Bitmap(front.Width, front.Height, PixelFormat.Format24bppRgb);
        shaded.SetResolution(150, 150);
        var paperBounds = new Rectangle(0, 0, shaded.Width, shaded.Height);
        using (var drawing = Graphics.FromImage(shaded))
        using (var shade = new System.Drawing.Drawing2D.LinearGradientBrush(paperBounds,
            Color.FromArgb(252, 250, 248), Color.FromArgb(224, 226, 222), 90))
            drawing.FillRectangle(shade, paperBounds);
        shaded.SetPixel(845, 150, Color.FromArgb(220, 219, 218));
        shaded.SetPixel(61, 1123, Color.FromArgb(210, 211, 209));
        using var nearEmpty = WithSpot(shaded, fringe, gray);
        Require(!PaperCorrection.IsUniformPale(nearEmpty) && ScanImageEvidence.IsEmptyPage(nearEmpty),
            "Gradual shading, isolated scanner specks and a thin attached corner shadow kept an otherwise empty sheet.");
        using var thinStroke = WithSpot(nearEmpty, new Rectangle(610, 820, 23, 1), Color.FromArgb(222, 222, 222));
        Require(!ScanImageEvidence.IsEmptyPage(thinStroke), "A faint one-pixel pencil stroke was treated as dust.");
        using var paleColor = WithSpot(nearEmpty, new Rectangle(620, 800, 8, 7), Color.FromArgb(234, 228, 253));
        Require(!ScanImageEvidence.IsEmptyPage(paleColor), "A low-contrast colored mark was skipped.");
        using var smallEdgeInk = WithSpot(nearEmpty, new Rectangle(0, 2, 13, 3), Color.FromArgb(78, 78, 78));
        Require(!ScanImageEvidence.IsEmptyPage(smallEdgeInk), "A dark mark at the very edge was treated as shading.");
        using var streaked = new Bitmap(1240, 1754, PixelFormat.Format24bppRgb);
        streaked.SetResolution(150, 150);
        using (var drawing = Graphics.FromImage(streaked))
        using (var rail = new Pen(Color.FromArgb(229, 230, 232), 4))
        {
            drawing.Clear(Color.White);
            drawing.DrawLine(rail, 32, 0, 10, streaked.Height - 1);
        }
        for (var y = 110; y < 1550; y += 155)
        for (var x = 125; x < 1100; x += 125)
            streaked.SetPixel(x, y, Color.FromArgb(220, 222, 221));
        Require(ScanImageEvidence.IsEmptyPage(streaked),
            "A full-length feeder streak or scattered scanner dust kept an otherwise blank back.");
        using var edgeAnnotation = WithSpot(streaked, new Rectangle(35, 720, 21, 1), Color.FromArgb(225, 225, 225));
        Require(!ScanImageEvidence.IsEmptyPage(edgeAnnotation), "The feeder streak hid independent writing near the edge.");

        using var punched = new Bitmap(front.Width, front.Height, PixelFormat.Format24bppRgb);
        punched.SetResolution(150, 150);
        using (var drawing = Graphics.FromImage(punched))
        using (var rail = new Pen(Color.FromArgb(228, 229, 231), 4))
        using (var fold = new SolidBrush(Color.FromArgb(215, 215, 215)))
        {
            drawing.Clear(Color.White);
            drawing.DrawLine(rail, 32, 0, 11, punched.Height - 1);
            drawing.FillEllipse(Brushes.DimGray, 1182, 650, 32, 13);
            drawing.FillEllipse(Brushes.DimGray, 1175, 1125, 31, 12);
            for (var x = 160; x < 1030; x += 110)
                drawing.FillRectangle(fold, x, 1170, 15, 2);
        }
        Require(ScanImageEvidence.InspectReverse(front, punched, detectShowThrough: false) == ReverseEvidence.Empty,
            "A blank punched reverse with feeder and fold lines was kept when show-through removal was off.");
        using var noRail = WithSpot(punched, new Rectangle(0, 0, 60, punched.Height), Color.White);
        Require(ScanImageEvidence.IsEmptyPage(noRail), "Aligned binder holes required a feeder streak or a scanned front to skip the page.");
        using var noRailWithInk = WithSpot(noRail, new Rectangle(490, 990, 25, 2), Color.FromArgb(229, 229, 229));
        Require(!ScanImageEvidence.IsEmptyPage(noRailWithInk), "The punched-page detector skipped a faint note without a feeder streak.");
        using var sloped = new Bitmap(punched.Width, punched.Height, PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(sloped))
        using (var fold = new Pen(Color.FromArgb(195, 195, 195), 2))
        {
            drawing.Clear(Color.White);
            drawing.FillEllipse(Brushes.DimGray, 1182, 650, 32, 13);
            drawing.FillEllipse(Brushes.DimGray, 1175, 1125, 31, 12);
            for (var x = 125; x < 1110; x += 67)
            {
                drawing.DrawLine(fold, x, 720 - x / 70, x + 15, 720 - (x + 15) / 70);
                drawing.DrawLine(fold, x, 1274 - x / 50, x + 15, 1274 - (x + 15) / 50);
            }
        }
        using var slopedWithSpeck = WithSpot(sloped, new Rectangle(724, 801, 3, 4), Color.FromArgb(255, 255, 220));
        Require(ScanImageEvidence.IsEmptyPage(slopedWithSpeck),
            "Two slanted creases and a tiny bright speck kept an otherwise empty punched sheet.");
        using var slopedWithNote = (Bitmap)slopedWithSpeck.Clone();
        using (var drawing = Graphics.FromImage(slopedWithNote))
        using (var pencil = new Pen(Color.FromArgb(205, 205, 205), 2))
            drawing.DrawLine(pencil, 530, 1249, 560, 1270);
        Require(!ScanImageEvidence.IsEmptyPage(slopedWithNote), "A pencil stroke crossing a slanted crease was skipped.");
        using var annotated = WithSpot(punched, new Rectangle(490, 990, 25, 2), Color.FromArgb(229, 229, 229));
        Require(ScanImageEvidence.InspectReverse(front, annotated, detectShowThrough: true) == ReverseEvidence.Content,
            "A faint annotation on punched paper was removed along with scan artifacts.");
        using var stamped = WithSpot(punched, new Rectangle(1179, 641, 16, 4), Color.FromArgb(185, 224, 247));
        Require(ScanImageEvidence.InspectReverse(front, stamped, detectShowThrough: true) == ReverseEvidence.Content,
            "A colored mark beside a binder hole was removed.");
        using var markedFold = (Bitmap)punched.Clone();
        using (var drawing = Graphics.FromImage(markedFold))
        using (var pen = new Pen(Color.FromArgb(197, 197, 197)))
            drawing.DrawLines(pen, new Point[] { new(488, 1171), new(493, 1173), new(513, 1169) });
        using var paleFoldMark = (Bitmap)punched.Clone();
        using (var drawing = Graphics.FromImage(paleFoldMark))
        using (var pen = new Pen(Color.FromArgb(222, 222, 222)))
            drawing.DrawLines(pen, new Point[] { new(488, 1171), new(493, 1173), new(513, 1169) });
        var paleFoldEvidence = ScanImageEvidence.InspectReverse(front, paleFoldMark, detectShowThrough: true);
        Require(paleFoldEvidence == ReverseEvidence.Ambiguous,
            $"A light pencil mark crossing a fold was classified as {paleFoldEvidence}.");
        var folded = queue.Begin(workspace, true, settings, new(), 2);
        queue.StorePage(folded.Id, 1, front);
        queue.StorePage(folded.Id, 2, markedFold);
        queue.FinishCapture(folded.Id);
        await queue.Prepare(folded.Id, CancellationToken.None);
        Require(queue.Get(folded.Id).Documents.Single() is
            { Disposition: QueueDisposition.Review, OmittedBacks: 0, DecisionPage: 2 },
            "A pencil mark crossing a fold was filed without the marked side.");

        var empty = queue.Begin(workspace, true, settings, new(DetectDocuments: true, SkipBlankBacks: false,
            RemoveBlankPages: true), 2);
        queue.StorePage(empty.Id, 1, nearEmpty);
        queue.StorePage(empty.Id, 2, streaked);
        queue.FinishCapture(empty.Id);
        await queue.Prepare(empty.Id, CancellationToken.None);
        var skipped = queue.Get(empty.Id);
        Require(skipped.IsFinished && queue.CanReprocess(skipped) && skipped.Documents.Single() is
            { Disposition: QueueDisposition.SkippedBlank, OmittedBacks: 2, Outputs: null },
            "A fully empty duplex sheet did not skip both pages and retain both originals.");

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
        var uncertainGroup = QueueRouting.Route([first, second, blank, first], false, new(DetectDocuments: true));
        Require(uncertainGroup is [{ DecisionPage: 2, Plan.ReviewReason: not null }, { Plan.Documents: [[_]] }],
            "A separator accidentally approved an uncertain boundary between its preceding pages.");
        var options = new QuickFilingOptions(DetectDocuments: true);
        var duplicateNumber = PageClues.FromText("Seite 1\nText der Mitteilung\nService | Seite 1 von 2", false);
        Require(duplicateNumber is { Number: 1, Total: 2 },
            "A short page number in the header erased the more precise footer.");
        var invoiceReference = PageClues.FromText("RECHNUNG\nBestellnummer: OR-00412\nRechnung Nr. INV-20263", false);
        Require(invoiceReference.Reference == "INV-20263",
            "A related order number overrode the invoice's own reference.");
        using var opening = first with
        {
            Clues = PageClues.FromText("Mitteilung\nSehr geehrte Damen und Herren,\nHiermit bestätigen wir den Eingang.", false)
        };
        using var closing = first with
        {
            Clues = PageClues.FromText("Diese Angaben gelten ab diesem Monat.\nMit freundlichen Grüßen\nIhr Serviceteam", false)
        };
        using var nextLetter = first with
        {
            Clues = PageClues.FromText("Bescheid\nGuten Tag,\nIhr Antrag ist eingegangen.\nMit besten Grüssen", false)
        };
        var letters = QueueRouting.Route([opening, blank, closing, blank, nextLetter, blank], true, options);
        Require(letters.Length == 2 && letters.Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([2, 1]),
            "An unnumbered letter was split or joined to the signed notice following it.");
        Require(QueueRouting.Route([opening, blank, nextLetter, blank], true, options)
            .Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([1, 1]),
            "Two headed letter openings were merged when the first letter had no closing.");
        using var shortNumbered = first with { Clues = duplicateNumber, Quality = new OcrQuality(12, 92) };
        using var nextNumbered = first with { Clues = PageClues.FromText("Information\nSeite 2 von 2", false) };
        var numbered = QueueRouting.Route([nextLetter, shortNumbered, nextNumbered], false, options);
        Require(numbered.Length == 2 && numbered.Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([1, 2]),
            "A short but reliable first-page footer could not separate the next numbered document.");
        using (var letterQueue = new FilingQueue(Path.Combine(workspace, "letter-queue")))
        {
            var destination = Directory.CreateDirectory(Path.Combine(workspace, "letters")).FullName;
            var printed = letterQueue.Begin(destination, true, settings, options, 3);
            using var letterStart = PrintedLetter("Mitteilung", "Sehr geehrte Damen und Herren,", closing: false);
            using var letterEnd = PrintedLetter("Weitere Informationen", "", closing: true);
            using var notice = PrintedLetter("Bescheid", "Guten Tag,", closing: true);
            Bitmap[] fronts = [letterStart, letterEnd, notice];
            for (var i = 0; i < fronts.Length; i++)
            {
                letterQueue.StorePage(printed.Id, 2 * i + 1, fronts[i]);
                letterQueue.StorePage(printed.Id, 2 * i + 2, i == 0 ? noRail : streaked);
            }
            letterQueue.FinishCapture(printed.Id);
            await letterQueue.Prepare(printed.Id, CancellationToken.None);
            await letterQueue.Transfer(printed.Id, CancellationToken.None);
            var filedLetter = letterQueue.Get(printed.Id);
            Require(filedLetter.FiledCount == 2 && filedLetter.Documents.Select(doc => doc.OmittedBacks).SequenceEqual([2, 1]),
                "The printed duplex letter and notice did not file separately with their empty backs skipped.");
            var counts = filedLetter.Documents.Select(doc =>
            {
                using var pdf = PdfReader.Open(doc.Outputs!.Single().FiledPath!, PdfDocumentOpenMode.Import);
                return pdf.PageCount;
            });
            Require(counts.SequenceEqual([2, 1]), "The filed PDFs did not retain the printed pages in order.");
        }
        var fixedBatch = QueueRouting.Route([first, blank, second, blank, first, second], true,
            new(SheetsPerDocument: 2));
        Require(fixedBatch.Select(doc => (doc.First, doc.Last, Count: doc.Plan.Documents.Single().Length))
            .SequenceEqual([(1, 4, 2), (5, 6, 2)]),
            "Fixed sheet counts split a duplex sheet or used the number of remaining pages.");
        var keepEmpty = options with { RemoveBlankPages = false };
        Require(QueueRouting.Route([blank, first, blank], false, new()).Single() is
                { ExcludedPages: [1, 3], Plan.OmittedBacks: 2 } skippedPages &&
                skippedPages.Plan.Documents.Single().Length == 1,
            "The default failed to skip verified empty pages in a single-sided batch.");
        Require(QueueRouting.Route([first, blank, second], false, keepEmpty)
                .Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([2, 1]),
            "Turning empty-page removal off lost a separator sheet.");
        Require(QueueRouting.Route([first, blank, blank, blank, second, blank], true, options)
                .Select(doc => doc.Plan.Documents.Single().Length).SequenceEqual([1, 1]),
            "A blank duplex sheet could not split the stack when both sides were verified.");
        using var uncertain = blank with { VerifiedBlank = false };
        Require(QueueRouting.Route([first, uncertain], false, new()).Single().Plan.Documents.Single().Length == 2,
            "A page was silently skipped based only on the appearance or OCR blank signal.");
        using var delivery = first with { Clues = PageClues.FromText("Lieferschein-Nr. LS-20261", false) };
        using var nextDelivery = first with { Clues = PageClues.FromText("Lieferschein-Nr. LS-20262", false) };
        var deliveries = QueueRouting.Route([delivery, nextDelivery], false, options);
        Require(deliveries.Length == 2 && deliveries.All(doc => doc.Plan.Documents is [[_]]),
            "Two distinct delivery note references required a repeated title to split.");
        using var footer = first with { Clues = PageClues.FromText("Continuation\n2/2", false) };
        Require(QueueRouting.Route([first, footer], false, options).Single().Plan.Documents.Single().Length == 2,
            "A compact final page number did not keep two pages together.");
        var backsWithFooters = QueueRouting.Route([first, blank, first, footer, second, footer, first, blank], true, options);
        Require(backsWithFooters.Select(doc => (doc.First, doc.Last, doc.Plan.OmittedBacks))
                .SequenceEqual([(1, 2, 1), (3, 4, 0), (5, 6, 0), (7, 8, 1)]) &&
            backsWithFooters.All(doc => doc.Plan.ReviewReason is null),
            "Repeated final-page numbers on duplex backs did not identify four documents around empty sides.");
        Require(!DocumentGroups.Plan([new(first.Clues), new(second.Clues),
                new(footer.Clues, PageNumberTrusted: false)])[1].Starts,
            "An unreadable page-number footer split the preceding unnumbered pages.");
        using var firstNumbered = first with { Clues = PageClues.FromText("Rechnung\nSeite 1 von 3", false) };
        using var differentRun = first with { Clues = PageClues.FromText("Rechnung\nSeite 1 von 2", false) };
        Require(DocumentGroups.Plan([new(firstNumbered.Clues), new(differentRun.Clues)])[1].Starts,
            "The same heading hid a page-number restart with a different total.");
        using var weakMiddle = second with { Quality = new OcrQuality(2, 42) };
        using var lastNumbered = first with { Clues = PageClues.FromText("Blatt 3 / 3", false) };
        Require(QueueRouting.Route([firstNumbered, weakMiddle, lastNumbered], false, options).Single().Plan
            is { ReviewReason: null, WeakOcrPages: 1 }, "A reliable page sequence was blocked by a weak middle page.");
        using var letter = first with { Clues = PageClues.FromText("Kontoauszug", false) };
        using var invoice = first with { Clues = PageClues.FromText("Rechnung\n1 von 1", false) };
        Require(QueueRouting.Route([letter, invoice], false, options).Length == 2,
            "A new title with restarted numbering did not split the document.");
        Require(QueueRouting.Route([invoice, letter], false, options).Length == 2,
            "A new title after an explicit final page did not split the document.");

        var ambiguous = queue.Begin(workspace, false, settings, options, 2);
        using var annotatedFront = WithSpot(front, new Rectangle(910, 900, 24, 24), Color.DarkBlue);
        queue.StorePage(ambiguous.Id, 1, front);
        queue.StorePage(ambiguous.Id, 2, annotatedFront);
        queue.StorePage(ambiguous.Id, 3, nearEmpty);
        queue.FinishCapture(ambiguous.Id);
        await queue.Prepare(ambiguous.Id, CancellationToken.None);
        Require(queue.Get(ambiguous.Id).Documents.Single() is { DecisionPage: 2, ReviewOmissions: [3] },
            "The ambiguous fixture did not preserve its boundary and empty page for review.");
        Console.WriteLine("Verified mirrored show-through, faint independent ink, paper edges, crop and OCR, original retention, and safe grouping.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Bitmap PrintedLetter(string title, string opening, bool closing)
    {
        var page = new Bitmap(1240, 1754, PixelFormat.Format24bppRgb);
        page.SetResolution(150, 150);
        using var drawing = Graphics.FromImage(page);
        using var heading = new Font("Arial", 41, FontStyle.Bold, GraphicsUnit.Pixel);
        using var copy = new Font("Arial", 30, FontStyle.Regular, GraphicsUnit.Pixel);
        drawing.Clear(Color.White);
        drawing.DrawString(title, heading, Brushes.Black, 125, 210);
        drawing.DrawString(opening, copy, Brushes.Black, 125, 360);
        for (var line = 0; line < 8; line++)
            drawing.DrawString("Bitte bewahren Sie diese Bestätigung für Ihre Unterlagen auf.",
                copy, Brushes.Black, 125, 460 + line * 88);
        if (closing)
            drawing.DrawString("Mit freundlichen Grüßen\nIhr Serviceteam", copy, Brushes.Black, 125, 1310);
        return page;
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
            await foreach (var result in engine.Scan(device, PaperSource.Duplex, 300, ScanPaper.Default, BitDepth.Color, deadline.Token))
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
