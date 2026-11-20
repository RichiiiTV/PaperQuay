using System.Text.Json;
using PdfSharpCore.Pdf.IO;
using PaperQuay;

internal static class QueueChecks
{
    private static readonly ProcessingOptions Settings = new(true, true, "deu");

    public static async Task Run(string workspace, string batchFixture, string reviewFixture)
    {
        var spool = Path.Combine(workspace, "spool");
        var missing = Path.Combine(workspace, "unavailable");
        var destination = Path.Combine(workspace, "destination");
        using var engine = new DocumentEngine();
        Guid job;
        Guid interrupted;
        using (var queue = new FilingQueue(spool))
        {
            try
            {
                using var second = new FilingQueue(spool);
                throw new Exception("Two writers could open the same queue.");
            }
            catch (IOException) { }

            job = queue.Begin(missing, true, Settings, new QuickFilingOptions(DetectDocuments: true), 3).Id;
            var pageNo = 0;
            var fixturePage = 0;
            await foreach (var source in engine.Import(batchFixture))
            {
                using (source)
                using (var bitmap = engine.Preview(source))
                {
                    if (++fixturePage is 2 or 4 or 6 or 7 or 8)
                    {
                        using var drawing = Graphics.FromImage(bitmap);
                        drawing.Clear(Color.White);
                    }
                    queue.StorePage(job, ++pageNo, bitmap);
                    if (pageNo != 6) continue;
                    using var weak = await GetFixturePage(engine, reviewFixture, 5);
                    queue.StorePage(job, ++pageNo, weak);
                    queue.StorePage(job, ++pageNo, bitmap);
                }
            }
            using (var blankBack = await GetFixturePage(engine, batchFixture, 2))
            {
                using var drawing = Graphics.FromImage(blankBack);
                drawing.Clear(Color.White);
                queue.StorePage(job, ++pageNo, blankBack);
            }
            var captured = queue.FinishCapture(job);
            Require(captured.Pages == 12 && captured.Phase == BatchPhase.Queued, "Capture did not enter the queue.");
            var processing = queue.Prepare(job, CancellationToken.None);
            Require(queue.Get(job).Phase == BatchPhase.Processing, "Processing did not start in the background.");
            var secondBatch = queue.Begin(missing, false, Settings, new(), 2);
            interrupted = secondBatch.Id;
            using (var unfinished = await GetFixturePage(engine, batchFixture, 1))
                queue.StorePage(interrupted, 1, unfinished);
            await processing;
            var prepared = queue.Get(job);
            Require(prepared.Documents.Select(doc => doc.Disposition)
                .SequenceEqual(new[] { QueueDisposition.Ready, QueueDisposition.Review, QueueDisposition.Ready }),
                "Independent documents did not stay eligible for automatic filing.");
            Require(!prepared.IsFinished, "A batch with unfiled scans was eligible for finished cleanup.");
            await queue.Transfer(job, CancellationToken.None);
            Require(queue.Get(job).HasPending && queue.Get(job).TransferError is not null &&
                queue.Get(job).FiledCount == 0, "An offline destination did not preserve pending delivery.");

        }
        using (var queue = new FilingQueue(spool))
        {
            var resumed = queue.Get(job);
            Require(resumed.Phase == BatchPhase.Planned && resumed.HasPending &&
                queue.NextTransfer()?.Id == job, "Waiting delivery did not recover after reopening the queue.");
            var restored = queue.Get(interrupted);
            Require(restored.Pages == 1 && restored.NeedsReview && !restored.HasPending,
                "Interrupted capture was not restored for manual review.");

            Directory.CreateDirectory(destination);
            queue.Redirect(job, destination);
            var firstOutput = resumed.Documents[0].Outputs![0];
            var pendingFile = Path.Combine(spool, job.ToString("N"), firstOutput.LocalFile);
            var postedFile = Path.Combine(destination, firstOutput.Name);
            var conflict = Path.Combine(destination, "existing.pdf");
            File.WriteAllText(conflict, "Original file content");
            try
            {
                FilingQueue.Publish(pendingFile, conflict, CancellationToken.None);
                throw new Exception("Delivery overwrote another file.");
            }
            catch (IOException) { }
            Require(File.ReadAllText(conflict) == "Original file content", "Existing destination bytes were changed.");
            File.Delete(conflict);
            File.WriteAllText(Path.Combine(destination, $".scan-{Path.GetFileNameWithoutExtension(pendingFile)}.tmp"),
                "Interrupted network copy");
            FilingQueue.Publish(pendingFile, postedFile, CancellationToken.None);
            await queue.Transfer(job, CancellationToken.None);
            var delivered = queue.Get(job);
            Require(delivered.FiledCount == 2 && Directory.GetFiles(destination, "*.pdf").Length == 2,
                "Idempotent delivery failed to account for a PDF written before a restart.");
            var review = delivered.Documents.Single(doc => doc.Disposition == QueueDisposition.Review);
            Require(File.Exists(queue.Original(job, review.FirstPage - 1)) &&
                File.Exists(queue.Original(job, review.LastPage + 1)),
                "The neighboring originals were removed before review.");

            var approved = new List<PreparedPage>();
            try
            {
                await foreach (var page in engine.Import(queue.Original(job, review.FirstPage)))
                {
                    using (page) approved.Add(await engine.Prepare(page, null, Settings, CancellationToken.None));
                }
                await queue.Approve(job, review.Number, [approved.ToArray()], CancellationToken.None);
                await queue.Transfer(job, CancellationToken.None);
            }
            finally { foreach (var page in approved) page.Dispose(); }
            Require(queue.Get(job).FiledCount == 3 && Directory.GetFiles(destination, "*.pdf").Length == 3 &&
                !Directory.GetFiles(destination, ".scan-*.tmp").Any(),
                "Approving the exception did not file exactly the third document.");
            Require(queue.CanReprocess(queue.Get(job)) && File.Exists(queue.Original(job, 1)),
                "A filed batch lost the ability to reprocess its original sides.");
        }
        using (var queue = new FilingQueue(spool))
        {
            Require(queue.Get(job).FiledCount == 3 && !queue.Get(job).HasPending &&
                queue.Get(interrupted).NeedsReview && queue.CanReprocess(queue.Get(job)),
                "Recent history or reprocessing did not survive a restart.");
            var empty = queue.Begin(destination, false, Settings, new(), 1).Id;
            queue.FinishCapture(empty, "No paper in feeder.");
            Require(queue.Batches.Where(batch => batch.IsFinished).Select(batch => batch.Id)
                .ToHashSet().SetEquals(new[] { job, empty }), "Cleanup included an unfinished batch.");
            Require(queue.RemoveBatch(job) && queue.RemoveBatch(empty), "Finished history was not removed.");
            Require(Directory.GetFiles(destination, "*.pdf").Length == 3 &&
                File.Exists(queue.Original(interrupted, 1)), "Cleanup removed filed PDFs or another batch's original.");
        }
        using (var queue = new FilingQueue(spool))
            Require(queue.Batches.Single().Id == interrupted, "Removed entries returned after restarting.");
        await VerifyLegacyRecovery(workspace, engine, reviewFixture);
        await VerifyStopAndResume(workspace, engine, reviewFixture);
        await VerifyOcrTimeout(workspace, reviewFixture);
        Console.WriteLine("Verified queue recovery, idempotent delivery, pause and resume, finished cleanup and an OCR timeout fallback.");
    }

    private static async Task VerifyStopAndResume(string workspace, DocumentEngine engine, string fixture)
    {
        var spool = Path.Combine(workspace, "paused");
        var destination = Path.GetFullPath(workspace);
        Guid id;
        using (var queue = new FilingQueue(spool))
        using (var stop = new CancellationTokenSource())
        {
            id = queue.Begin(destination, false, Settings, new(), 1).Id;
            using (var image = await GetFixturePage(engine, fixture, 3))
                queue.StorePage(id, 1, image);
            queue.FinishCapture(id);
            var processing = queue.Prepare(id, CancellationToken.None, pause: stop.Token);
            try
            {
                queue.RemoveBatch(id);
                throw new Exception("A processing batch could be removed.");
            }
            catch (InvalidOperationException) { }
            stop.Cancel();
            await processing;
            Require(queue.Get(id).Phase == BatchPhase.Paused && queue.NextPreparation() is null &&
                File.Exists(queue.Original(id, 1)), "Stopping a batch lost the page or automatically restarted.");
        }
        using (var queue = new FilingQueue(spool))
        {
            Require(queue.Get(id).Phase == BatchPhase.Paused, "Restart lost the pause.");
            queue.Resume(id);
            Require(queue.NextPreparation()?.Id == id, "A paused batch could not resume.");
            var progress = new PageProgress();
            await queue.Prepare(id, CancellationToken.None, progress);
            Require(progress.Done == 1 && queue.Get(id).HasPending, "Resume did not process the page.");
            Require(queue.RemoveBatch(id) && !Directory.Exists(Path.Combine(spool, id.ToString("N"))),
                "The selected local batch was not removed.");
        }
    }

    private sealed class PageProgress : IProgress<int>
    {
        public int Done { get; private set; }
        public void Report(int count) => Done = count;
    }

    private static async Task VerifyOcrTimeout(string workspace, string fixture)
    {
        using var engine = new DocumentEngine(TimeSpan.FromMilliseconds(1));
        await foreach (var page in engine.Import(fixture))
        {
            using (page)
            using (var result = await engine.Prepare(page, 0, Settings, CancellationToken.None))
            {
                Require(result.OcrUnavailable && result.NeedsReview && !result.Clues.Blank,
                    "A timed-out page must be retained with an OCR warning.");
                var path = Path.Combine(workspace, "timed-out.pdf");
                DocumentEngine.Merge(path, [result]);
                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Require(pdf.PageCount == 1, "A timed-out page could not be exported.");
            }
            return;
        }
        throw new Exception("Missing OCR timeout fixture.");
    }

    private static async Task VerifyLegacyRecovery(string workspace, DocumentEngine engine, string fixture)
    {
        var spool = Path.Combine(workspace, "legacy");
        var destination = Directory.CreateDirectory(Path.Combine(workspace, "recovered")).FullName;
        QueueBatch held;
        QueueBatch filed;
        using (var queue = new FilingQueue(spool))
        {
            var batch = queue.Begin(destination, true, Settings, new(DetectDocuments: true), 2);
            using (var front = await GetFixturePage(engine, fixture, 3))
                queue.StorePage(batch.Id, 1, front);
            using (var back = await GetFixturePage(engine, fixture, 5))
                queue.StorePage(batch.Id, 2, back);
            held = queue.FinishCapture(batch.Id) with
            {
                Phase = BatchPhase.Planned,
                Documents = [new QueueDocument(1, 1, 2, QueueDisposition.Review,
                    "OCR flagged 1 page(s). Use Next OCR issue to review them.")]
            };
        }
        File.WriteAllText(Path.Combine(spool, held.Id.ToString("N"), "batch.json"),
            JsonSerializer.Serialize(held, new JsonSerializerOptions { IgnoreReadOnlyProperties = true }));

        using (var queue = new FilingQueue(spool))
        {
            Require(queue.NextPreparation()?.Id == held.Id, "A legacy hold with intact originals was not requeued.");
            await queue.Prepare(held.Id, CancellationToken.None);
            var document = queue.Get(held.Id).Documents.Single();
            Require(document.Disposition == QueueDisposition.Ready && document.WeakOcrPages == 1,
                "The pages did not become one PDF with an OCR warning.");
            await queue.Transfer(held.Id, CancellationToken.None);
            var output = queue.Get(held.Id).Documents.Single().Outputs!.Single().FiledPath!;
            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Require(pdf.PageCount == 2, "The recovered PDF lost one of the original sides.");
            filed = queue.Get(held.Id);
        }
        File.WriteAllText(Path.Combine(spool, held.Id.ToString("N"), "batch.json"),
            JsonSerializer.Serialize(filed with { OriginalsUntil = DateTimeOffset.UtcNow.AddSeconds(-1) },
                new JsonSerializerOptions { IgnoreReadOnlyProperties = true }));
        using (var queue = new FilingQueue(spool))
        {
            Require(queue.Get(held.Id).FiledCount == 1 && queue.NextPreparation() is null,
                "Restart reprocessed a batch that already filed.");
            Require(!queue.CanReprocess(queue.Get(held.Id)) && !File.Exists(queue.Original(held.Id, 1)) &&
                Directory.GetFiles(destination, "*.pdf").Length == 1, "Expired recovery affected a filed PDF or retained raw pages.");
        }
    }

    private static async Task<Bitmap> GetFixturePage(DocumentEngine engine, string fixture, int requested)
    {
        var index = 0;
        await foreach (var page in engine.Import(fixture))
        {
            using (page)
                if (++index == requested) return engine.Preview(page);
        }
        throw new Exception("Expected page not found in the fixture.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
