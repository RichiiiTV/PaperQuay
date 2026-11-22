using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using NAPS2.Images;

namespace PaperQuay;

internal enum BatchPhase { Capturing, Queued, Processing, Planned, Empty, Paused }
internal enum QueueDisposition { Review, Ready, Discarded, SkippedBlank }

internal sealed record CaptureLayout(SheetSide Side, int Sheet);
internal sealed record PageEdit(int Source, int? Rotation = null, bool Crop = false,
    bool? BreakBefore = null, bool KeepBlank = false, bool Exclude = false);
internal sealed record ReviewDraft(ProcessingOptions Processing, PageEdit[] Pages);

internal sealed record QueueOutput(string LocalFile, string Name, string? FiledPath = null, string? FiledHash = null,
    ReviewDraft? Source = null);
internal sealed record QueueDocument(int Number, int FirstPage, int LastPage, QueueDisposition Disposition,
    string? Reason = null, QueueOutput[]? Outputs = null, int OmittedBacks = 0, int WeakOcrPages = 0,
    int[]? ReviewOmissions = null, bool FromAutomaticPages = false, int? DecisionPage = null, ReviewDraft? Draft = null);
internal sealed record QueueBatch(Guid Id, DateTimeOffset Created, string Destination, bool Duplex,
    ProcessingOptions Processing, QuickFilingOptions Filing, int Workers)
{
    public BatchPhase Phase { get; init; }
    public int Pages { get; init; }
    public string? ScanError { get; init; }
    public string? TransferError { get; init; }
    public DateTimeOffset RetryAfter { get; init; }
    public DateTimeOffset? OriginalsUntil { get; init; }
    public bool? HistoryHidden { get; init; }
    public bool ReviewBeforeFiling { get; init; }
    public QueueDocument[] Documents { get; init; } = [];
    public bool NeedsReview => Documents.Any(doc => doc.Disposition == QueueDisposition.Review);
    public bool HasPending => Documents.Any(doc => doc.Outputs?.Any(file => file.FiledPath is null) == true);
    public int FiledCount => Documents.Sum(doc => doc.Outputs?.Count(file => file.FiledPath is not null) ?? 0);
    public bool IsFinished => Phase == BatchPhase.Empty || (Phase == BatchPhase.Planned && !NeedsReview && !HasPending);
}

// Queue state is advanced on the UI thread. Workers only process immutable batch snapshots and files.
internal sealed partial class FilingQueue : IDisposable
{
    private sealed record CaptureCompletion(string? Error, ReviewDraft? Draft);
    private readonly string root;
    private readonly FileStream ownership;
    private readonly Dictionary<Guid, QueueBatch> batches = [];
    private readonly Dictionary<Guid, (QueueBatch Batch, DateTimeOffset RetryAt)> preparationSaves = [];
    private readonly HashSet<Guid> hiddenHistory = [];
    private DateTimeOffset nextPrune;
    private static readonly TimeSpan OriginalRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan FinishedCacheRetention = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    public string? RecoveryMessage { get; private set; }

    public FilingQueue(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        ownership = new FileStream(Path.Combine(this.root, "queue.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var directory in Directory.GetDirectories(this.root))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".removed-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name[9..], "N", out _))
                {
                    DeleteRemoved(directory);
                    continue;
                }
                if (!Guid.TryParseExact(name, "N", out var id))
                    continue;
                try
                {
                    CheckBatchDirectory(directory);
                    var batch = JsonSerializer.Deserialize<QueueBatch>(File.ReadAllText(Manifest(id)), Json);
                    if (batch?.Id != id || string.IsNullOrWhiteSpace(batch.Destination) ||
                        !Path.IsPathFullyQualified(batch.Destination) || batch.Documents is null ||
                        batch.Workers is < 1 or > 4 || batch.Pages < 0)
                        throw new InvalidDataException("Invalid batch record.");
                    batches.Add(id, batch);
                    if (batch.Phase == BatchPhase.Capturing)
                        RecoverCapture(id);
                    else if (batch.Phase == BatchPhase.Processing)
                        SavePreparation(batch with { Phase = BatchPhase.Queued });
                    else if (batch.Phase == BatchPhase.Planned)
                    {
                        if (CanRecheckLegacy(batch))
                            Store(batch with { Phase = BatchPhase.Queued, Documents = [] });
                        else if (batch.HasPending)
                            batches[id] = batch with { RetryAfter = DateTimeOffset.MinValue };
                        else
                            ReleaseFinished(batch);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    RecoveryMessage = "A batch record could not be read. Its files were kept in the local queue.";
                }
            }
            try
            {
                hiddenHistory = JsonSerializer.Deserialize<HashSet<Guid>>(
                    File.ReadAllText(Path.Combine(this.root, "hidden-history.json")), Json) ?? [];
            }
            catch (FileNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                RecoveryMessage ??= "The saved history view could not be read. All scan records are visible.";
            }
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    public IEnumerable<QueueBatch> Batches => batches.Values.OrderByDescending(batch => batch.Created);
    public IEnumerable<QueueBatch> History => Batches.Where(batch => !(batch.HistoryHidden ?? hiddenHistory.Contains(batch.Id)));
    public QueueBatch Get(Guid id) => batches[id];

    public async Task<(int Failed, string? Error)> ClearHistory(QueueBatch[] history)
    {
        var failed = 0;
        string? error = null;
        foreach (var batch in history)
        {
            var files = batch.Documents.SelectMany(doc => doc.Outputs ?? []).Where(file => file.FiledPath is not null).ToArray();
            var deleted = new HashSet<QueueOutput>();
            if (files.Length > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            DeleteFiledPdf(batch.Id, file);
                            deleted.Add(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                            InvalidDataException or NotSupportedException)
                        {
                            failed++;
                            error ??= $"{file.Name}. {ex.Message}";
                        }
                    }
                });
            }
            var current = Get(batch.Id);
            var documents = current.Documents.Select(doc => doc with
            {
                Outputs = doc.Outputs?.Where(file => !deleted.Contains(file)).ToArray()
            }).ToArray();
            try
            {
                Store(current with
                {
                    Documents = documents,
                    HistoryHidden = !documents.Any(doc => doc.Outputs?.Any(file => file.FiledPath is not null) == true)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                error ??= $"Couldn't save the updated batch record. {ex.Message}";
            }
        }
        return (failed, error);
    }

    private static void DeleteFiledPdf(Guid id, QueueOutput file)
    {
        var path = file.FiledPath!;
        var name = SafeFileName(file.Name);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("Scan ", StringComparison.Ordinal) || !name.Contains($" {id.ToString("N")[..8]} - ", StringComparison.Ordinal))
            throw new IOException("The recorded path does not match this scan.");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The saved PDF path now points to a link.");
            if (file.FiledHash is { } expected)
            {
                using var input = File.OpenRead(path);
                if (Convert.ToHexString(SHA256.HashData(input)) != expected)
                    throw new IOException("The PDF has changed since it was saved.");
            }
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
    }

    public bool CanReprocess(QueueBatch batch) => batch.IsFinished && HasOriginals(batch, 1, batch.Pages);

    public bool HasOriginals(QueueBatch batch, int first, int last) =>
        first > 0 && last >= first && last <= batch.Pages &&
        (!batch.IsFinished || batch.OriginalsUntil > DateTimeOffset.UtcNow) &&
        Enumerable.Range(first, last - first + 1).All(page => File.Exists(Original(batch.Id, page)));

    public void PruneExpiredOriginals(Guid? inUse = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (nextPrune > now) return;
        nextPrune = now.AddHours(1);
        foreach (var batch in batches.Values.Where(batch => batch.Id != inUse &&
                     batch.OriginalsUntil is { } expiry && !KeepFinishedCache(expiry, now)).ToArray())
        {
            try { ReleaseFinished(batch); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecoveryMessage = "Some retained scan files could not be cleaned up.";
            }
        }
    }

    private bool CanRecheckLegacy(QueueBatch batch) =>
        batch.ScanError is null && batch.Pages > 0 &&
        batch.Documents is [{ Disposition: QueueDisposition.Review, FirstPage: 1, Outputs: null } document] &&
        document.LastPage == batch.Pages &&
        (document.Reason?.StartsWith("OCR flagged ", StringComparison.Ordinal) == true ||
         document.Reason?.EndsWith("could be incomplete or contain several documents. Review the grouping.",
             StringComparison.Ordinal) == true) &&
        Enumerable.Range(1, batch.Pages).All(page => File.Exists(Original(batch.Id, page)));

    public QueueBatch Begin(string destination, bool duplex, ProcessingOptions processing,
        QuickFilingOptions filing, int workers, bool reviewBeforeFiling = false)
    {
        var batch = new QueueBatch(Guid.NewGuid(), DateTimeOffset.Now, Path.GetFullPath(destination),
            duplex, processing, filing, Math.Clamp(workers, 1, 4)) { ReviewBeforeFiling = reviewBeforeFiling };
        Directory.CreateDirectory(Folder(batch.Id));
        Store(batch);
        return batch;
    }

    public QueueBatch AppendReview(Guid id)
    {
        var batch = Get(id);
        if (!batch.ReviewBeforeFiling || batch.Phase != BatchPhase.Planned || !batch.NeedsReview || batch.HasPending)
            throw new InvalidOperationException("This batch is no longer accepting review pages.");
        File.Delete(CompletionPath(id));
        batch = batch with { Phase = BatchPhase.Capturing };
        Store(batch);
        return batch;
    }

    public void StorePage(Guid id, int page, Bitmap image, CaptureLayout? layout = null)
    {
        var path = Original(id, page);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                image.Save(stream, ImageFormat.Png);
                stream.Flush(flushToDisk: true);
            }
            if (layout is not null) WriteRecord(LayoutPath(id, page), layout);
            File.Move(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void RecoverCapture(Guid id)
    {
        var completion = new CaptureCompletion("The app closed during capture. Check that all pages are present.", null);
        var path = CompletionPath(id);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<CaptureCompletion>(File.ReadAllText(path), Json) is { } saved)
                completion = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        FinishCapture(id, completion.Error, draft: completion.Draft);
    }

    public QueueBatch FinishCapture(Guid id, string? error = null, int? duplexStart = null, ReviewDraft? draft = null)
    {
        var batch = Get(id);
        var pages = Directory.EnumerateFiles(Folder(id), "page-*.png").Count();
        if ((!batch.ReviewBeforeFiling && batch.Duplex && pages % 2 != 0) ||
            (duplexStart is { } start && (pages - start) % 2 != 0))
            error ??= "The scanner returned an odd number of sides in duplex mode. Check for the missing side.";
        var needsReview = pages > 0 && (error is not null || batch.ReviewBeforeFiling);
        if (draft is null && batch.ReviewBeforeFiling && batch.Documents.FirstOrDefault()?.Draft is { } saved)
            draft = saved with { Pages = [.. saved.Pages, .. Enumerable.Range(batch.Pages + 1, Math.Max(0, pages - batch.Pages))
                .Select(page => new PageEdit(page))] };
        var updated = batch with
        {
            Pages = pages, ScanError = error,
            Phase = pages == 0 ? BatchPhase.Empty : needsReview ? BatchPhase.Planned : BatchPhase.Queued,
            Documents = needsReview
                ? [new QueueDocument(1, 1, pages, QueueDisposition.Review, error ?? "Review this batch before filing.", Draft: draft)] : []
        };
        if (!SavePreparation(updated))
        {
            try { WriteRecord(CompletionPath(id), new CaptureCompletion(error, draft)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return updated;
    }

    public QueueBatch? NextPreparation() => Batches.Reverse().FirstOrDefault(batch =>
        batch.Phase == BatchPhase.Queued && !IsSavingPreparation(batch.Id));

    public void Resume(Guid id)
    {
        var batch = Get(id);
        if (batch.Phase == BatchPhase.Paused)
            Store(batch with { Phase = BatchPhase.Queued });
    }

    public void PauseQueued(Guid id)
    {
        var batch = Get(id);
        if (batch.Phase == BatchPhase.Queued)
            Store(batch with { Phase = BatchPhase.Paused });
    }

    public async Task Prepare(Guid id, CancellationToken shutdown, IProgress<int>? progress = null,
        CancellationToken pause = default, IProgress<string>? stage = null)
    {
        var batch = Get(id);
        if (batch.Phase != BatchPhase.Queued) return;
        if (batch.Duplex && batch.Pages % 2 != 0)
        {
            FinishCapture(id, "The scanner returned an odd number of sides in duplex mode. Check for the missing side.");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, pause);
        var token = cancellation.Token;
        Store(batch with { Phase = BatchPhase.Processing });
        QueueBatch result;
        try
        {
            // A dedicated context keeps scanner capture independent of queued imports, rendering and OCR.
            var documents = await Task.Run(() => Analyze(batch, progress, stage, token), token);
            result = Get(id) with
            {
                Phase = BatchPhase.Planned, Documents = documents,
                OriginalsUntil = documents.All(doc => doc.Disposition == QueueDisposition.SkippedBlank)
                    ? DateTimeOffset.UtcNow + OriginalRetention : null
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = Get(id) with { Phase = pause.IsCancellationRequested ? BatchPhase.Paused : BatchPhase.Queued };
        }
        catch (Exception ex)
        {
            result = Get(id) with
            {
                Phase = BatchPhase.Planned,
                Documents = [new QueueDocument(1, 1, batch.Pages, QueueDisposition.Review,
                    $"Batch processing stopped. {ex.Message}")]
            };
        }
        SavePreparation(result);
    }

    public bool IsSavingPreparation(Guid id) => preparationSaves.ContainsKey(id);

    public bool RetryPreparationSaves(Guid? immediately = null)
    {
        var changed = false;
        foreach (var pending in preparationSaves.Values.ToArray())
            if (immediately == pending.Batch.Id ||
                (immediately is null && pending.RetryAt <= DateTimeOffset.UtcNow))
                changed |= SavePreparation(pending.Batch with { HistoryHidden = Get(pending.Batch.Id).HistoryHidden });
        return changed;
    }

    private bool SavePreparation(QueueBatch batch)
    {
        try
        {
            Store(batch);
            preparationSaves.Remove(batch.Id);
            TryDelete(CompletionPath(batch.Id));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            preparationSaves[batch.Id] = (batch, DateTimeOffset.UtcNow.AddSeconds(3));
            return false;
        }
    }

    private async Task<QueueDocument[]> Analyze(QueueBatch batch, IProgress<int>? progress,
        IProgress<string>? stage, CancellationToken token)
    {
        using var engine = new DocumentEngine();
        using var slots = new SemaphoreSlim(batch.Workers);
        var prepared = new PreparedPage?[batch.Pages];
        var staged = new List<QueueDocument>();
        var complete = 0;
        try
        {
            stage?.Report("Recognizing");
            var sides = batch.Duplex ? 2 : 1;
            await Parallel.ForEachAsync(Enumerable.Range(0, batch.Pages / sides), Workers(batch, token),
                async (sheet, canceled) =>
                {
                    var first = sheet * sides + 1;
                    await PrepareSheet(engine, batch, first, slots, (number, page) =>
                    {
                        prepared[number - 1] = page;
                        progress?.Report(Interlocked.Increment(ref complete));
                    }, canceled);
                });
            stage?.Report("Building PDFs");
            var pages = prepared.Select(page => page!).ToArray();
            var drafts = QueueRouting.Route(pages, batch.Duplex, batch.Filing);
            foreach (var doc in drafts)
            {
                token.ThrowIfCancellationRequested();
                var document = new QueueDocument(doc.Number, doc.First, doc.Last, QueueDisposition.Review,
                    doc.Plan.ReviewReason, OmittedBacks: doc.Plan.OmittedBacks, WeakOcrPages: doc.Plan.WeakOcrPages,
                    ReviewOmissions: doc.ExcludedPages, FromAutomaticPages: true, DecisionPage: doc.DecisionPage);
                if (doc.Plan.ReviewReason is not null)
                    staged.Add(document);
                else if (doc.Plan.Documents.Length == 0)
                    staged.Add(document with { Disposition = QueueDisposition.SkippedBlank, FromAutomaticPages = false,
                        Reason = "All captured pages were empty. No PDF was created." });
                else
                {
                    var source = Enumerable.Range(doc.First, doc.Last - doc.First + 1).Except(doc.ExcludedPages ?? [])
                        .Select(number => new PageEdit(number, pages[number - 1].Orientation, KeepBlank: true)).ToArray();
                    staged.Add(Stage(batch, document, doc.Plan.Documents, doc.Plan.OmittedBacks, token,
                        [new ReviewDraft(batch.Processing, source)]));
                }
            }
            return staged.ToArray();
        }
        catch
        {
            foreach (var output in staged.SelectMany(doc => doc.Outputs ?? []))
                TryDelete(SpoolFile(batch.Id, output.LocalFile));
            throw;
        }
        finally
        {
            foreach (var page in prepared) page?.Dispose();
        }
    }

    private QueueDocument Stage(QueueBatch batch, QueueDocument document, PreparedPage[][] sets,
        int omitted, CancellationToken token, ReviewDraft[]? sources)
    {
        if (sets.Length == 0 || sets.Any(set => set.Length == 0))
            throw new InvalidOperationException("There are no pages to file.");
        if (sources is not null && (sources.Length != sets.Length ||
            sources.Where((source, index) => source.Pages.Length != sets[index].Length).Any()))
            throw new InvalidOperationException("The saved page selections do not match the PDFs.");
        var files = new List<QueueOutput>();
        try
        {
            for (var index = 0; index < sets.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var name = $"Scan {batch.Created:yyyy-MM-dd HHmmss} {batch.Id.ToString("N")[..8]} - {document.Number:D2}";
                if (sets.Length > 1) name += $"-{index + 1:D2}";
                var output = new QueueOutput($"file-{Guid.NewGuid():N}.pdf", name + ".pdf", Source: sources?[index]);
                files.Add(output);
                DocumentEngine.Merge(SpoolFile(batch.Id, output.LocalFile), sets[index], token);
            }
            token.ThrowIfCancellationRequested();
            return document with { Disposition = QueueDisposition.Ready, Reason = null, Outputs = files.ToArray(), OmittedBacks = omitted };
        }
        catch
        {
            foreach (var file in files) TryDelete(SpoolFile(batch.Id, file.LocalFile));
            throw;
        }
    }

    public void SaveDraft(Guid id, int number, ReviewDraft draft)
    {
        var batch = Get(id);
        var document = batch.Documents.Single(doc => doc.Number == number && doc.Disposition == QueueDisposition.Review);
        Store(batch with { Documents = Replace(batch.Documents, document with { Draft = draft }) });
    }

    public async Task Approve(Guid id, int number, PreparedPage[][] sets, CancellationToken token, int? omitted = null,
        ReviewDraft[]? sources = null)
    {
        var batch = Get(id);
        var document = batch.Documents.Single(doc => doc.Number == number && doc.Disposition == QueueDisposition.Review);
        var ready = await Task.Run(() => Stage(batch, document with
        {
            ReviewOmissions = null, FromAutomaticPages = false, DecisionPage = null, Draft = null
        }, sets, omitted ?? document.OmittedBacks, token, sources), token);
        var current = Get(id);
        try
        {
            Store(current with
            {
                Documents = Replace(current.Documents, ready),
                RetryAfter = DateTimeOffset.MinValue
            });
        }
        catch
        {
            foreach (var file in ready.Outputs!) TryDelete(SpoolFile(id, file.LocalFile));
            throw;
        }
    }

    public void Discard(Guid id, int number)
    {
        var current = Get(id);
        var document = current.Documents.Single(doc => doc.Number == number && doc.Disposition == QueueDisposition.Review);
        var updated = current with
        {
            Documents = Replace(current.Documents, document with { Disposition = QueueDisposition.Discarded, Draft = null })
        };
        Store(updated);
        ReleaseFinished(updated);
    }

    public QueueBatch? NextTransfer(IReadOnlySet<Guid>? active = null, Guid? changingDestination = null)
    {
        var destinations = (active ?? new HashSet<Guid>()).Select(id => Path.TrimEndingDirectorySeparator(Get(id).Destination))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Batches.Reverse().FirstOrDefault(batch => batch.HasPending && batch.RetryAfter <= DateTimeOffset.Now &&
            batch.Id != changingDestination && !destinations.Contains(Path.TrimEndingDirectorySeparator(batch.Destination)));
    }

    public async Task Transfer(Guid id, CancellationToken token)
    {
        var batch = Get(id);
        var allFiles = batch.Documents.SelectMany(doc => (doc.Outputs ?? []).Select(file => (doc.Number, File: file))).ToArray();
        foreach (var (number, file) in allFiles.Where(item => item.File.FiledPath is null))
        {
            token.ThrowIfCancellationRequested();
            batch = Get(id);
            var destination = Path.Combine(batch.Destination, SafeFileName(file.Name));
            try
            {
                var hash = await Task.Run(() => Publish(SpoolFile(id, file.LocalFile), destination, token), token);
                var current = Get(id);
                var group = current.Documents.Single(doc => doc.Number == number);
                var filed = group with { Outputs = group.Outputs!.Select(item =>
                    item.LocalFile == file.LocalFile ? item with { FiledPath = destination, FiledHash = hash } : item).ToArray() };
                Store(current with
                {
                    Documents = Replace(current.Documents, filed), TransferError = null, HistoryHidden = false,
                    RetryAfter = DateTimeOffset.MinValue
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var reason = ex switch
                {
                    DirectoryNotFoundException => "The save folder is unavailable. Delivery will retry automatically.",
                    UnauthorizedAccessException => "The save folder denied write access. Check it or change the destination.",
                    _ => ex.Message
                };
                Store(Get(id) with { TransferError = reason, RetryAfter = DateTimeOffset.Now.AddSeconds(30) });
                return;
            }
        }
        ReleaseFinished(Get(id));
    }

    internal static string Publish(string localFile, string destination, CancellationToken token)
    {
        string? MatchingHash()
        {
            using var source = File.OpenRead(localFile);
            using var existing = File.OpenRead(destination);
            if (source.Length != existing.Length) return null;
            var hash = SHA256.HashData(source);
            return hash.SequenceEqual(SHA256.HashData(existing)) ? Convert.ToHexString(hash) : null;
        }

        token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".scan-{Path.GetFileNameWithoutExtension(localFile)}.tmp");
        if (File.Exists(destination))
        {
            if (MatchingHash() is { } existingHash)
            {
                TryDelete(temporary);
                return existingHash;
            }
            throw new IOException("A different PDF already has the target name. Choose another folder or resolve the conflict.");
        }
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var source = File.OpenRead(localFile))
            // The stable staging name lets a retry replace its own incomplete copy after a crash.
            using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }
                target.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination) && MatchingHash() is not null) { }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void Retry(Guid id) => Store(Get(id) with { RetryAfter = DateTimeOffset.MinValue });

    public void Redirect(Guid id, string directory) => Store(Get(id) with
    {
        Destination = Path.GetFullPath(directory), RetryAfter = DateTimeOffset.MinValue, TransferError = null
    });

    public bool RemoveBatch(Guid id)
    {
        var batch = Get(id);
        if (batch.Phase is BatchPhase.Capturing or BatchPhase.Processing)
            throw new InvalidOperationException("Stop this batch before removing it.");
        var folder = Folder(id);
        CheckBatchDirectory(folder);
        var removed = Path.Combine(root, ".removed-" + id.ToString("N"));
        Directory.Move(folder, removed);
        batches.Remove(id);
        return DeleteRemoved(removed);
    }

    private static bool DeleteRemoved(string folder)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateDirectories(folder).Any())
                return false;
            foreach (var file in Directory.EnumerateFiles(folder))
                File.Delete(file);
            Directory.Delete(folder);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public string Original(Guid id, int number) => Path.Combine(Folder(id), $"page-{number:D6}.png");
    public CaptureLayout PageLayout(QueueBatch batch, int number)
    {
        var path = LayoutPath(batch.Id, number);
        if (batch.ReviewBeforeFiling && File.Exists(path))
            return JsonSerializer.Deserialize<CaptureLayout>(File.ReadAllText(path), Json) ??
                throw new InvalidDataException($"The scan-side record for page {number} is invalid.");
        return batch.Duplex ? new CaptureLayout(number % 2 == 0 ? SheetSide.Back : SheetSide.Front, (number + 1) / 2) :
            new CaptureLayout(SheetSide.Unpaired, number);
    }

    private string Folder(Guid id) => Path.Combine(root, id.ToString("N"));
    private string Manifest(Guid id) => Path.Combine(Folder(id), "batch.json");
    private string CompletionPath(Guid id) => Path.Combine(Folder(id), "capture-complete.json");
    private string LayoutPath(Guid id, int number) => Original(id, number) + ".layout.json";
    private string SpoolFile(Guid id, string file) => Path.Combine(Folder(id), SafeFileName(file));
    private static string SafeFileName(string file) =>
        file.Length > 0 && Path.GetFileName(file) == file && file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? file : throw new InvalidDataException("Invalid PDF name in queue record.");

    private void Store(QueueBatch batch)
    {
        WriteRecord(Manifest(batch.Id), batch);
        batches[batch.Id] = batch;
    }

    private static void WriteRecord<T>(string path, T record)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void ReleaseFinished(QueueBatch batch)
    {
        if (batch.Phase != BatchPhase.Planned || !batch.IsFinished)
            return;
        var folder = Folder(batch.Id);
        CheckBatchDirectory(folder);
        if ((batch.FiledCount > 0 || batch.Documents.Any(doc => doc.Disposition == QueueDisposition.SkippedBlank)) &&
            batch.OriginalsUntil is null && File.Exists(Original(batch.Id, 1)))
        {
            batch = batch with { OriginalsUntil = DateTimeOffset.UtcNow + OriginalRetention };
            Store(batch);
        }
        var now = DateTimeOffset.UtcNow;
        var keepOriginals = batch.OriginalsUntil > now;
        var keepCache = batch.OriginalsUntil is { } expiry && KeepFinishedCache(expiry, now);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name != "batch.json" && !(keepCache && name.StartsWith(CachePrefix, StringComparison.Ordinal)) &&
                !(keepOriginals && name.StartsWith("page-", StringComparison.Ordinal) &&
                    (name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".png.layout.json", StringComparison.Ordinal))))
                TryDelete(file);
        }
    }

    private static bool KeepFinishedCache(DateTimeOffset originalsExpire, DateTimeOffset now) =>
        originalsExpire > now + OriginalRetention - FinishedCacheRetention;

    private static void CheckBatchDirectory(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("This batch folder points outside the queue.");
    }

    private static QueueDocument[] Replace(QueueDocument[] documents, QueueDocument replacement) =>
        documents.Select(doc => doc.Number == replacement.Number ? replacement : doc).ToArray();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => ownership.Dispose();
}
