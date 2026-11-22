using System.Text.Json;
using PdfSharpCore.Pdf.IO;
using PaperQuay;

internal static class QueueChecks
{
    private static readonly ProcessingOptions Settings = new(true, true, "deu");

    public static async Task ExportFixture(string folder, PreparedPage[][] documents)
    {
        using var queue = new FilingQueue(Path.Combine(folder, "queue"));
        var draft = queue.Begin(folder, false, Settings, new(), 1, reviewBeforeFiling: true);
        using var page = new Bitmap(12, 12);
        queue.StorePage(draft.Id, 1, page);
        queue.FinishCapture(draft.Id);
        await queue.Approve(draft.Id, 1, documents, CancellationToken.None);
        await queue.Transfer(draft.Id, CancellationToken.None);
        Require(queue.Get(draft.Id).FiledCount == documents.Length, "An approved fixture did not file every PDF.");
    }

    public static async Task Run(string workspace, string batchFixture, string reviewFixture)
    {
        var spool = Path.Combine(workspace, "spool");
        var missing = Path.Combine(workspace, "unavailable");
        var destination = Path.Combine(workspace, "destination");
        using var engine = new DocumentEngine();
        Guid job;
        Guid interrupted;
        Guid later;
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
            await queue.Redirect(job, destination);
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
            var stale = Path.Combine(destination, $".scan-{Path.GetFileNameWithoutExtension(pendingFile)}.tmp");
            File.WriteAllText(stale, "Interrupted network copy");
            FilingQueue.Publish(pendingFile, postedFile, CancellationToken.None);
            await queue.Transfer(job, CancellationToken.None);
            var delivered = queue.Get(job);
            Require(delivered.FiledCount == 2 && Directory.GetFiles(destination, "*.pdf").Length == 2 &&
                File.ReadAllText(stale) == "Interrupted network copy",
                "Idempotent delivery lost the earlier PDF or touched a staging file it did not create.");
            File.Delete(stale);
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
        var manifest = Path.Combine(spool, job.ToString("N"), "batch.json");
        var persisted = JsonSerializer.Deserialize<QueueBatch>(File.ReadAllText(manifest))!;
        var legacy = persisted.Documents[^1];
        persisted.Documents[^1] = legacy with { Outputs = [legacy.Outputs!.Single() with { FiledHash = null }] };
        File.WriteAllText(manifest, JsonSerializer.Serialize(persisted));
        using (var queue = new FilingQueue(spool))
        {
            Require(queue.Get(job).FiledCount == 3 && !queue.Get(job).HasPending &&
                queue.Get(interrupted).NeedsReview && queue.CanReprocess(queue.Get(job)),
                "Recent history or reprocessing did not survive a restart.");
            var empty = queue.Begin(destination, false, Settings, new(), 1).Id;
            queue.FinishCapture(empty, "No paper in feeder.");
            Require(queue.Batches.Where(batch => batch.IsFinished).Select(batch => batch.Id)
                .ToHashSet().SetEquals(new[] { job, empty }), "Cleanup included an unfinished batch.");
            var paths = queue.Get(job).Documents.SelectMany(doc => doc.Outputs!).Select(file => file.FiledPath!).ToArray();
            var replacement = paths[0];
            var original = File.ReadAllBytes(replacement);
            var delivered = queue.Get(job).Documents[0].Outputs![0];
            var snapshot = engine.SnapshotFiledPdf(delivered, CancellationToken.None);
            Require(File.ReadAllBytes(snapshot).SequenceEqual(original), "The verified PDF snapshot changed the contents.");
            File.WriteAllText(replacement, "Different PDF added after filing");
            try
            {
                engine.SnapshotFiledPdf(delivered, CancellationToken.None);
                throw new InvalidOperationException("History opened a changed PDF as if it were the original.");
            }
            catch (IOException) { }
            Require(File.ReadAllBytes(snapshot).SequenceEqual(original), "The opened copy still depended on the shared PDF.");
            DocumentEngine.DeleteScratchFile(snapshot);
            File.WriteAllText(paths[2], "Unverified replacement file");
            var unrelated = Path.Combine(destination, "Unrelated.pdf");
            File.WriteAllText(unrelated, "Unrelated file");
            using (File.Open(paths[1], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var partiallyCleared = await queue.ClearHistory(queue.History.ToArray());
                Require(partiallyCleared.Failed == 2 && queue.History.Single().Id == job &&
                    File.ReadAllText(replacement) == "Different PDF added after filing" && File.Exists(paths[1]) &&
                    partiallyCleared.Unverified == 1 && File.ReadAllText(paths[2]) == "Unverified replacement file" &&
                    queue.Get(job).FiledCount == 2,
                    "Clearing history hid a failure or deleted an unverified replacement.");
            }
            File.WriteAllBytes(replacement, original);
            var cleared = await queue.ClearHistory(queue.History.ToArray());
            Require(cleared.Failed == 0 && !queue.History.Any() && paths.Take(2).All(path => !File.Exists(path)) &&
                queue.CanReprocess(queue.Get(job)) && queue.Get(interrupted).NeedsReview &&
                File.Exists(queue.Original(interrupted, 1)) && File.Exists(unrelated),
                "Clearing history did not delete the saved scans or changed files outside the confirmed records.");
            Require(queue.RemoveBatch(job) && queue.RemoveBatch(empty), "Finished history was not removed.");
            Require(Directory.GetFiles(destination, "*.pdf").Length == 2 && File.Exists(paths[2]) &&
                File.Exists(queue.Original(interrupted, 1)), "Cleanup removed filed PDFs or another batch's original.");
            later = queue.Begin(destination, false, Settings, new(), 1).Id;
            queue.FinishCapture(later);
        }
        using (var queue = new FilingQueue(spool))
        {
            Require(queue.History.Single().Id == later &&
                queue.Batches.Select(batch => batch.Id).ToHashSet().SetEquals(new[] { interrupted, later }),
                "Cleared or deleted history returned after restarting, or a new batch was hidden.");
            Require(queue.Get(interrupted).NeedsReview, "A cleared batch lost its review after restarting.");
            await queue.Redirect(interrupted, destination);
            await foreach (var page in engine.Import(queue.Original(interrupted, 1)))
            {
                using (page)
                using (var prepared = await engine.Prepare(page, null, Settings, CancellationToken.None))
                    await queue.Approve(interrupted, 1, [[prepared]], CancellationToken.None);
            }
            await queue.Transfer(interrupted, CancellationToken.None);
            Require(queue.History.Any(batch => batch.Id == interrupted && batch.FiledCount == 1),
                "A newly delivered file stayed hidden after its pending batch was cleared from History.");
        }
        await VerifyLegacyRecovery(workspace, engine, reviewFixture);
        await VerifyStopAndResume(workspace, engine, reviewFixture);
        await VerifyOcrTimeout(workspace, reviewFixture);
        await VerifyPreparationSaveRetry(workspace);
        VerifyCaptureSaveRetry(workspace);
        await VerifyReviewDraftRecovery(workspace);
        await VerifyRedirectAfterPublication(workspace);
        VerifyDeliverySelection(workspace);
        await VerifyDriverDiscovery();
        Console.WriteLine("Verified queue recovery, idempotent delivery, pause and resume, finished cleanup and an OCR timeout fallback.");
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static async Task VerifyPreparationSaveRetry(string workspace)
    {
        using var queue = new FilingQueue(Path.Combine(workspace, "locked-status"));
        var batch = queue.Begin(workspace, false, Settings, new(), 1);
        using var page = new Bitmap(400, 580);
        using (var drawing = Graphics.FromImage(page)) drawing.Clear(Color.White);
        queue.StorePage(batch.Id, 1, page);
        queue.FinishCapture(batch.Id);
        FileStream? held = null;
        try
        {
            var manifest = Path.Combine(Path.GetDirectoryName(queue.Original(batch.Id, 1))!, "batch.json");
            await queue.Prepare(batch.Id, CancellationToken.None, stage: new InlineProgress(stage =>
            {
                if (stage == "Building PDFs") held = File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            }));
            Require(queue.IsSavingPreparation(batch.Id) && queue.Get(batch.Id).Phase == BatchPhase.Processing,
                "A locked manifest did not hold the completed result for retry.");
        }
        finally { held?.Dispose(); }
        Require(queue.RetryPreparationSaves(batch.Id) && !queue.IsSavingPreparation(batch.Id) &&
            queue.Get(batch.Id).Documents.Single().Disposition == QueueDisposition.SkippedBlank &&
            File.Exists(queue.Original(batch.Id, 1)),
            "Releasing the manifest lock did not save the original result without reprocessing.");
    }

    private static void VerifyCaptureSaveRetry(string workspace)
    {
        var spool = Path.Combine(workspace, "capture-record-lock");
        Guid afterRestart;
        Guid copyAfterRestart;
        var edit = new PageEdit(1, Rotation: 90, Crop: true);
        using (var queue = new FilingQueue(spool))
        {
            using var page = new Bitmap(24, 30);
            Guid LockedCapture(ReviewDraft? draft = null)
            {
                var id = queue.Begin(workspace, false, Settings, new(), 1, reviewBeforeFiling: draft is not null).Id;
                queue.StorePage(id, 1, page);
                var manifest = Path.Combine(Path.GetDirectoryName(queue.Original(id, 1))!, "batch.json");
                using (File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
                    queue.FinishCapture(id, draft: draft);
                Require(queue.IsSavingPreparation(id) && queue.Get(id).Phase == BatchPhase.Capturing,
                    "The locked capture record was not kept for retry.");
                return id;
            }
            var retryInPlace = LockedCapture();
            Require(queue.RetryPreparationSaves(retryInPlace) && queue.NextPreparation()?.Id == retryInPlace,
                "The saved capture did not enter processing after the lock cleared.");
            afterRestart = LockedCapture();
            copyAfterRestart = LockedCapture(new ReviewDraft(Settings, [edit]));
        }
        using var recovered = new FilingQueue(spool);
        Require(recovered.Get(afterRestart) is { Phase: BatchPhase.Queued, Pages: 1, ScanError: null },
            "Restarting changed a completed capture into an interrupted scan.");
        Require(recovered.Get(copyAfterRestart).Documents.Single().Draft?.Pages.Single() == edit,
            "A locked capture record lost the recovered copy's edits.");
    }

    private static async Task VerifyReviewDraftRecovery(string workspace)
    {
        var spool = Path.Combine(workspace, "review-draft");
        var destination = Path.Combine(workspace, "recoverable-destination");
        Guid id;
        var edited = new PageEdit(2, 90, Crop: true, BreakBefore: true, KeepBlank: true);
        using (var queue = new FilingQueue(spool))
        {
            id = queue.Begin(destination, true, Settings, new(), 2, reviewBeforeFiling: true).Id;
            using var page = new Bitmap(24, 30);
            queue.StorePage(id, 1, page, new CaptureLayout(SheetSide.Front, 1));
            queue.StorePage(id, 2, page, new CaptureLayout(SheetSide.Back, 1));
            var captured = queue.FinishCapture(id);
            Require(captured.NeedsReview && !captured.HasPending && queue.NextPreparation() is null,
                "A user-requested review went straight to delivery.");
            queue.SaveDraft(id, 1, new ReviewDraft(Settings, [edited, new PageEdit(1, Exclude: true)]));
            queue.AppendReview(id);
            queue.StorePage(id, 3, page, new CaptureLayout(SheetSide.Unpaired, 3));
        }

        using var reopened = new FilingQueue(spool);
        var recovered = reopened.Get(id);
        Require(recovered.Documents.Single().Draft?.Pages.SequenceEqual([edited, new PageEdit(1, Exclude: true), new PageEdit(3)]) == true &&
            reopened.PageLayout(recovered, 2) == new CaptureLayout(SheetSide.Back, 1) &&
            reopened.PageLayout(recovered, 3) == new CaptureLayout(SheetSide.Unpaired, 3),
            "An interrupted append lost draft edits, source order, or duplex layout.");

        var path = Path.Combine(workspace, "draft-content.pdf");
        using (var pdf = new PdfSharpCore.Pdf.PdfDocument())
        {
            pdf.AddPage();
            pdf.Save(path);
        }
        var pagePdf = new PreparedPage("", path, PageClues.FromText("Test", false), "Test", default, 0, true,
            OwnsFiles: false, PdfImageReady: true);
        ReviewDraft[] source = [new(Settings, [edited]), new(Settings, [new PageEdit(3)])];
        await reopened.Approve(id, 1, [[pagePdf], [pagePdf]], CancellationToken.None, sources: source);
        await reopened.Transfer(id, CancellationToken.None);
        var pending = reopened.Get(id);
        Require(pending.HasPending && pending.TransferError is not null && pending.Documents.Single().Draft is null,
            "An offline destination lost an approved review or kept it editable for duplicate approval.");
        Directory.CreateDirectory(destination);
        var first = pending.Documents.Single().Outputs![0];
        var spoolPdf = Path.Combine(Path.GetDirectoryName(reopened.Original(id, 1))!, first.LocalFile);
        FilingQueue.Publish(spoolPdf, Path.Combine(destination, first.Name), CancellationToken.None);
        await reopened.Transfer(id, CancellationToken.None);
        var filed = reopened.Get(id);
        Require(filed.FiledCount == 2 && Directory.GetFiles(destination, "*.pdf").Length == 2 &&
            filed.Documents.Single().Outputs!.Select(file => file.Source!.Pages.Single()).SequenceEqual([edited, new PageEdit(3)]),
            "Delivery retry duplicated a PDF or lost its exact source page and edits.");
    }

    private static async Task VerifyRedirectAfterPublication(string workspace)
    {
        foreach (var legacy in new[] { false, true })
        {
            var root = Directory.CreateDirectory(Path.Combine(workspace, legacy ? "legacy-redirect" : "recorded-redirect")).FullName;
            var previous = Directory.CreateDirectory(Path.Combine(root, "previous")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var id = Guid.NewGuid();
            var spool = Path.Combine(root, "spool");
            var folder = Directory.CreateDirectory(Path.Combine(spool, id.ToString("N"))).FullName;
            var firstPath = Path.Combine(previous, "first.pdf");
            QueueOutput first = new("first-local.pdf", "first.pdf", AttemptedPath: legacy ? null : firstPath);
            QueueOutput second = new("second-local.pdf", "second.pdf");
            var record = new QueueBatch(id, DateTimeOffset.UtcNow, previous, false, Settings, new(), 1)
            {
                Phase = BatchPhase.Planned,
                Pages = 1,
                Documents = [new QueueDocument(1, 1, 1, QueueDisposition.Ready, Outputs: [first, second])]
            };
            File.WriteAllText(Path.Combine(folder, "batch.json"), JsonSerializer.Serialize(record));
            foreach (var file in new[] { first, second })
            {
                using var pdf = new PdfSharpCore.Pdf.PdfDocument();
                pdf.AddPage();
                pdf.Save(Path.Combine(folder, file.LocalFile));
            }
            var hash = FilingQueue.Publish(Path.Combine(folder, first.LocalFile), firstPath, CancellationToken.None);
            using var queue = new FilingQueue(spool);
            await queue.Redirect(id, destination);
            Require(queue.Get(id).Documents.Single().Outputs![0] is { FiledPath: not null } saved &&
                saved.FiledPath == firstPath && saved.FiledHash == hash,
                "Redirecting lost a PDF that reached the previous folder before the queue save.");
            await queue.Transfer(id, CancellationToken.None);
            Require(queue.Get(id).FiledCount == 2 && Directory.GetFiles(previous).SequenceEqual([firstPath]) &&
                Directory.GetFiles(destination).SequenceEqual([Path.Combine(destination, second.Name)]),
                "Redirecting copied a previously filed PDF again or lost an undelivered PDF.");
        }
    }

    private static void VerifyDeliverySelection(string workspace)
    {
        var spool = Path.Combine(workspace, "delivery-selection");
        var share = Path.Combine(workspace, "slow");
        Guid[] ids;
        using (var seed = new FilingQueue(spool))
        {
            ids = new[] { share, share + Path.DirectorySeparatorChar, Path.Combine(workspace, "healthy") }
                .Select((folder, index) =>
                {
                    var batch = seed.Begin(folder, false, Settings, new(), 1) with
                    {
                        Created = DateTimeOffset.UtcNow.AddMinutes(index),
                        Phase = BatchPhase.Planned,
                        Documents = [new QueueDocument(1, 1, 1, QueueDisposition.Ready,
                            Outputs: [new QueueOutput("local.pdf", "scan.pdf")])]
                    };
                    File.WriteAllText(Path.Combine(spool, batch.Id.ToString("N"), "batch.json"),
                        JsonSerializer.Serialize(batch));
                    return batch.Id;
                }).ToArray();
        }
        using var queue = new FilingQueue(spool);
        Require(queue.NextTransfer()?.Id == ids[0] &&
            queue.NextTransfer(new HashSet<Guid> { ids[0] })?.Id == ids[2] &&
            queue.NextTransfer(new HashSet<Guid> { ids[0] }, ids[2]) is null,
            "A busy destination blocked a different destination or took a second delivery slot.");
    }

    private static async Task VerifyDriverDiscovery()
    {
        var fast = new List<NAPS2.Scan.ScanDevice>();
        var slow = new TaskCompletionSource<List<NAPS2.Scan.ScanDevice>>();
        await using (var probes = DocumentEngine.AwaitDriverProbes([Task.FromResult(fast), slow.Task],
            TimeSpan.FromMilliseconds(50), CancellationToken.None).GetAsyncEnumerator())
        {
            Require(await probes.MoveNextAsync() && ReferenceEquals(probes.Current, fast) &&
                await probes.MoveNextAsync() && probes.Current is null,
                "A working driver was not reported while a second probe stalled.");
        }
        using var stop = new CancellationTokenSource();
        await using var canceled = DocumentEngine.AwaitDriverProbes([slow.Task], TimeSpan.FromMinutes(1),
            stop.Token).GetAsyncEnumerator();
        stop.Cancel();
        try
        {
            await canceled.MoveNextAsync();
            throw new InvalidOperationException("Cancel did not interrupt a stalled scanner discovery.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
        var manifest = Path.Combine(spool, held.Id.ToString("N"), "batch.json");
        File.WriteAllText(manifest,
            JsonSerializer.Serialize(filed with { OriginalsUntil = DateTimeOffset.UtcNow.AddDays(7).AddHours(-2) },
                new JsonSerializerOptions { IgnoreReadOnlyProperties = true }));
        using (var queue = new FilingQueue(spool))
            Require(queue.CanReprocess(queue.Get(held.Id)) && queue.ReadPrepared(held.Id, 1) is null,
                "The finished processing cache outlived its retention window or evicted the originals.");
        File.WriteAllText(manifest,
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
