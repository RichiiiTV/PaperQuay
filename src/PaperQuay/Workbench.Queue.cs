using System.Diagnostics;
using System.Threading.Channels;
using NAPS2.Scan;

namespace PaperQuay;

internal sealed partial class Workbench
{
    private readonly FilingQueue filingQueue;
    private readonly System.Windows.Forms.Timer queueTimer = new() { Interval = 3000 };
    private readonly CancellationTokenSource queueLifetime = new();
    private readonly ListView batchList = QueueList("Scan batches", ("Batch", 155), ("Pages", 70),
        ("Status", 163), ("Filed PDFs", 90), ("Destination", 510));
    private readonly ListView documentList = QueueList("Documents in selected batch", ("Document", 97),
        ("Pages", 72), ("Status", 155), ("PDF or review reason", 630));
    private readonly Label batchDetails = Caption("Scanned batches will appear here.");
    private Button reviewQueued = null!;
    private Button reviewNext = null!;
    private Button openQueued = null!;
    private Button openDestination = null!;
    private Button retryDelivery = null!;
    private Button redirectDelivery = null!;
    private Button discardQueued = null!;
    private Button stopPreparation = null!;
    private Button removeBatch = null!;
    private Button clearFinished = null!;
    private Button reprocess = null!;
    private bool updatingQueue;
    private Guid? lastQueuedSelection;
    private (Guid Id, CancellationTokenSource Stop)? preparing;
    private int preparedPages;
    private long preparationStarted;
    private string preparationStage = "Recognizing";
    private Guid? transferring;
    private Guid? choosingNewDestination;
    private (Guid Batch, int Document)? reviewing;
    private Guid? reprocessing;
    private (Guid Id, QueueDocument Document)? automaticReview;
    private Button? restoreSides;
    private bool ShowingQueue => activeScreen is Screen.History or Screen.Decisions;

    private sealed record DocumentRow(int Number, QueueOutput? Output);
    private Guid? SelectedBatch => batchList.SelectedItems.Count == 1 && batchList.SelectedItems[0].Tag is Guid id ? id : null;
    private DocumentRow? SelectedDocument => documentList.SelectedItems.Count == 1
        ? documentList.SelectedItems[0].Tag as DocumentRow : null;

    private static ListView QueueList(string name, params (string Name, int Width)[] columns)
    {
        var view = new ListView
        {
            AccessibleName = name, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White, ForeColor = Ink
        };
        foreach (var (title, width) in columns) view.Columns.Add(title, width);
        return view;
    }

    private Control BuildQueueView()
    {
        reviewQueued = Action("Review document", ReviewQueued, primary: true);
        reviewNext = Action("Review next", ReviewNext);
        openQueued = Action("Open PDF", OpenQueued);
        openDestination = Action("Open folder", OpenQueuedFolder);
        retryDelivery = Action("Retry now", RetryQueued);
        redirectDelivery = Action("Change destination…", RedirectQueued);
        discardQueued = Action("Discard document…", DiscardQueued);
        stopPreparation = Action("Stop processing", StopQueued);
        removeBatch = Action("Remove batch…", RemoveQueuedBatch);
        clearFinished = Action("Clear finished…", ClearFinishedBatches);
        reprocess = Action("Reprocess originals", ReprocessOriginals);
        var batchActions = Row();
        batchActions.Controls.AddRange([stopPreparation, retryDelivery, redirectDelivery, reprocess, removeBatch, clearFinished]);
        var buttons = Row();
        buttons.Controls.AddRange([reviewQueued, reviewNext, openQueued, openDestination, discardQueued]);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 53));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(batchList);
        body.Controls.Add(batchActions);
        body.Controls.Add(Caption("Documents in selected batch"));
        body.Controls.Add(documentList);
        body.Controls.Add(buttons);
        batchDetails.MaximumSize = new Size(955, 68);
        body.Controls.Add(batchDetails);
        batchList.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingQueue) RefreshDocuments();
        };
        documentList.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingQueue) UpdateQueueActions();
        };
        documentList.DoubleClick += async (_, _) =>
        {
            if (openQueued.Enabled) await OpenQueued();
            else if (reviewQueued.Enabled) await ReviewQueued();
        };
        return body;
    }

    private void StartQueue()
    {
        if (filingQueue.RecoveryMessage is { } message) status.Text = message;
        RefreshQueue();
        queueTimer.Tick += (_, _) =>
        {
            PumpQueue();
            if (ShowingQueue && preparing is { } active && SelectedBatch == active.Id)
                batchDetails.Text = PreparationDetails(filingQueue.Get(active.Id));
        };
        queueTimer.Start();
        PumpQueue();
    }

    private void PumpQueue()
    {
        if (queueLifetime.IsCancellationRequested) return;
        filingQueue.PruneExpiredOriginals(reprocessing);
        if (preparing is null && filingQueue.NextPreparation() is { } pending)
            _ = PrepareQueued(pending.Id);
        if (transferring is null && filingQueue.NextTransfer() is { } ready && ready.Id != choosingNewDestination)
            _ = TransferQueued(ready.Id);
    }

    private async Task PrepareQueued(Guid id)
    {
        using var stop = new CancellationTokenSource();
        var progress = BeginPreparation(id, stop);
        var stage = new Progress<string>(value =>
        {
            if (preparing?.Stop != stop || IsDisposed) return;
            preparationStage = value;
            ShowPreparationProgress(id);
        });
        var advanced = false;
        try
        {
            var processing = filingQueue.Prepare(id, queueLifetime.Token, progress, stop.Token, stage);
            RefreshQueue();
            await processing;
            advanced = true;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) status.Text = $"Queue processing needs attention. {ex.Message}";
        }
        finally
        {
            preparing = null;
            if (!IsDisposed)
            {
                RefreshQueue();
                if (advanced) PumpQueue();
            }
        }
    }

    private IProgress<int> BeginPreparation(Guid id, CancellationTokenSource stop)
    {
        preparing = (id, stop);
        preparedPages = 0;
        preparationStage = "Recognizing";
        preparationStarted = Stopwatch.GetTimestamp();
        return new Progress<int>(count =>
        {
            if (preparing?.Stop != stop || IsDisposed) return;
            preparedPages = Math.Max(count, preparedPages);
            ShowPreparationProgress(id);
        });
    }

    private void ShowPreparationProgress(Guid id)
    {
        if (!ShowingQueue) return;
        var batch = filingQueue.Get(id);
        var row = batchList.Items.Cast<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, id));
        if (row is not null) row.SubItems[2].Text = BatchStatus(batch);
        if (SelectedBatch == id) batchDetails.Text = PreparationDetails(batch);
    }

    private async Task PrepareCapturedSheets(QueueBatch batch, ChannelReader<int> sheets)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(queueLifetime.Token);
        var progress = BeginPreparation(batch.Id, stop);
        try
        {
            await Task.Run(() => filingQueue.PrepareDuringCapture(batch, sheets.ReadAllAsync(stop.Token),
                progress, stop.Token), stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            // The normal queue path retries any sheet that could not be prepared during capture.
        }
        finally
        {
            preparing = null;
            if (!IsDisposed)
            {
                RefreshQueue();
                PumpQueue();
            }
        }
    }

    private async Task TransferQueued(Guid id)
    {
        transferring = id;
        var advanced = false;
        try
        {
            RefreshQueue();
            await filingQueue.Transfer(id, queueLifetime.Token);
            advanced = true;
        }
        catch (OperationCanceledException) when (queueLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed) status.Text = $"Queue delivery needs attention. {ex.Message}";
        }
        finally
        {
            transferring = null;
            if (!IsDisposed)
            {
                RefreshQueue();
                if (advanced) PumpQueue();
            }
        }
    }

    private Task QuickScan() => Run("Connecting to the scanner…", async () =>
    {
        var device = await SelectScanner();
        var duplex = sides.SelectedIndex == 0;
        var dpi = ScanDpi;
        var token = operation!.Token;
        token.ThrowIfCancellationRequested();
        var batch = filingQueue.Begin(saveFolder, duplex, GetProcessingOptions(), GetQuickChoices(), workers.SelectedIndex + 1);
        var early = preparing is null && filingQueue.NextPreparation() is null ? Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions { SingleWriter = true }) : null;
        if (early is not null) _ = PrepareCapturedSheets(batch, early.Reader);
        RefreshQueue();
        var count = 0;
        string? failure = null;
        status.Text = "Capturing pages for the local queue…";
        try
        {
            await foreach (var page in engine.Scan(device, duplex ? PaperSource.Duplex : PaperSource.Feeder, dpi, token))
            {
                using (page)
                {
                    var pageNumber = count + 1;
                    await Task.Run(() =>
                    {
                        using var image = engine.Preview(page);
                        filingQueue.StorePage(batch.Id, pageNumber, image);
                    }, token);
                    count = pageNumber;
                    if (!duplex || count % 2 == 0)
                        early?.Writer.TryWrite(duplex ? count - 1 : count);
                    status.Text = $"Captured {count} page(s)…";
                }
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            failure = "Capture was canceled. Check that all pages are present.";
        }
        catch (Exception ex)
        {
            failure = $"Capture stopped. Check that all pages are present. {ex.Message}";
        }
        QueueBatch completed;
        try { completed = filingQueue.FinishCapture(batch.Id, failure); }
        finally { early?.Writer.TryComplete(); }
        failure = completed.ScanError;
        RefreshQueue(batch.Id);
        PumpQueue();
        status.Text = completed.Pages == 0 ? failure ?? "The scanner returned no pages." :
            failure is null ? $"Queued {completed.Pages} page(s). The feeder is ready for the next load." :
                $"{completed.Pages} captured page(s) are available for review. {failure}";
    });

    private void RefreshQueue(Guid? select = null)
    {
        if (reviewQueued is null || IsDisposed) return;
        var items = filingQueue.Batches.ToArray();
        var flagged = items.Sum(batch => batch.Documents.Count(doc => doc.Disposition == QueueDisposition.Review));
        waitingKpi.Text = items.Count(batch => batch.Phase is BatchPhase.Capturing or BatchPhase.Queued or BatchPhase.Processing ||
            batch.HasPending).ToString();
        reviewKpi.Text = flagged.ToString();
        filedKpi.Text = items.Sum(batch => batch.FiledCount).ToString();
        nav[Screen.Decisions].Text = flagged == 0 ? "Needs a decision" : $"Needs a decision  ({flagged})";
        if (select is not null) lastQueuedSelection = select;
        if (!ShowingQueue) return;
        var previous = select ?? lastQueuedSelection ?? SelectedBatch;
        lastQueuedSelection = null;
        updatingQueue = true;
        batchList.BeginUpdate();
        try
        {
            batchList.Items.Clear();
            foreach (var batch in items.Where(batch => activeScreen == Screen.History || batch.NeedsReview))
            {
                var state = BatchStatus(batch);
                var row = new ListViewItem([batch.Created.LocalDateTime.ToString("dd MMM · HH:mm:ss"),
                    batch.Pages == 0 ? "—" : batch.Pages.ToString(), state, batch.FiledCount.ToString(), batch.Destination])
                {
                    Tag = batch.Id,
                    ForeColor = batch.NeedsReview ? Color.FromArgb(143, 80, 24) : Ink
                };
                batchList.Items.Add(row);
                if (batch.Id == previous) row.Selected = true;
            }
            if (batchList.SelectedItems.Count == 0 && batchList.Items.Count != 0)
                batchList.Items[0].Selected = true;
        }
        finally
        {
            batchList.EndUpdate();
            updatingQueue = false;
        }
        RefreshDocuments();
    }

    private string BatchStatus(QueueBatch batch) => batch.Phase switch
    {
        BatchPhase.Capturing when preparing?.Id == batch.Id && preparedPages > 0 => $"Scanning · {preparedPages} ready",
        BatchPhase.Capturing => "Scanning",
        BatchPhase.Queued when preparing?.Id == batch.Id && preparing?.Stop.IsCancellationRequested == false
            => $"Recognizing {preparedPages}/{batch.Pages}",
        BatchPhase.Queued => "Queued",
        BatchPhase.Processing when preparing?.Stop.IsCancellationRequested == true => "Stopping…",
        BatchPhase.Processing => preparationStage == "Building PDFs" ? preparationStage : $"Recognizing {preparedPages}/{batch.Pages}",
        BatchPhase.Paused => "Paused",
        BatchPhase.Empty => "No pages",
        _ when transferring == batch.Id => "Filing",
        _ when batch.NeedsReview && batch.TransferError is not null => "Review + waiting",
        _ when batch.NeedsReview => "Needs a decision",
        _ when batch.TransferError is not null => "Waiting for folder",
        _ when batch.HasPending => "Ready to file",
        _ when batch.FiledCount > 0 => "Filed",
        _ => "Closed"
    };

    private string PreparationDetails(QueueBatch batch)
    {
        if (preparing?.Stop.IsCancellationRequested == true)
            return "Stopping processing. The original pages will stay in the queue.";
        var elapsed = Stopwatch.GetElapsedTime(preparationStarted);
        var stage = batch.Phase == BatchPhase.Capturing ? $"Recognized or checked {preparedPages} sides while scanning" :
            preparationStage == "Building PDFs" ? preparationStage : $"Recognized or checked {preparedPages} of {batch.Pages} sides";
        return $"{stage} · {elapsed:mm\\:ss} elapsed";
    }

    private void RefreshDocuments()
    {
        var previous = SelectedDocument;
        updatingQueue = true;
        documentList.BeginUpdate();
        try
        {
            documentList.Items.Clear();
            if (SelectedBatch is { } id)
            {
                var batch = filingQueue.Get(id);
                var omitted = batch.Documents.Sum(doc => doc.OmittedBacks);
                batchDetails.Text = preparing?.Id == id && batch.Phase != BatchPhase.Paused ? PreparationDetails(batch) :
                    batch.Phase == BatchPhase.Paused ? "Original pages are kept locally. Resume processing or remove this batch." :
                    batch.TransferError ?? batch.ScanError ??
                    (omitted == 0 ? batch.Destination : $"{batch.Destination} · {omitted} blank or show-through side(s) omitted");
                foreach (var doc in batch.Documents.Where(doc => activeScreen == Screen.History ||
                    doc.Disposition == QueueDisposition.Review))
                {
                    var range = doc.FirstPage == doc.LastPage ? doc.FirstPage.ToString() : $"{doc.FirstPage}–{doc.LastPage}";
                    if (doc.Outputs is { Length: > 0 })
                    {
                        foreach (var file in doc.Outputs)
                        {
                            var outcome = file.FiledPath is not null ? "Filed" :
                                batch.TransferError is not null ? "Waiting for folder" :
                                transferring == id ? "Filing" : "Ready";
                            var details = doc.WeakOcrPages == 0 ? file.Name :
                                $"Weak OCR on {doc.WeakOcrPages} page(s) · {file.Name}";
                            AddDocumentRow(doc.Number, range, outcome, details, file, previous);
                        }
                    }
                    else
                    {
                        var disposition = doc.Disposition == QueueDisposition.Review ? "Needs a decision" : "Discarded";
                        AddDocumentRow(doc.Number, range, disposition, doc.Reason ?? "", null, previous);
                    }
                }
                if (documentList.SelectedItems.Count == 0 && documentList.Items.Count > 0)
                    documentList.Items[0].Selected = true;
            }
            else batchDetails.Text = activeScreen == Screen.Decisions ? "No scans need a decision." : "No scans in history.";
        }
        finally
        {
            documentList.EndUpdate();
            updatingQueue = false;
        }
        UpdateQueueActions();
    }

    private void AddDocumentRow(int number, string pages, string state, string details, QueueOutput? file, DocumentRow? previous)
    {
        var row = new ListViewItem([$"{number:D2}", pages, state, details])
        {
            Tag = new DocumentRow(number, file),
            ForeColor = state == "Needs a decision" ? Color.FromArgb(143, 80, 24) : Ink
        };
        documentList.Items.Add(row);
        if (previous?.Number == number && previous.Output?.LocalFile == file?.LocalFile) row.Selected = true;
    }

    private void UpdateQueueActions()
    {
        if (reviewQueued is null) return;
        var batch = SelectedBatch is { } id ? filingQueue.Get(id) : null;
        var selected = SelectedDocument;
        var issue = selected is not null && batch?.Documents.Any(doc => doc.Number == selected.Number &&
            doc.Disposition == QueueDisposition.Review) == true;
        var cached = selected is not null && batch?.Documents.Any(doc => doc.Number == selected.Number &&
            doc.FromAutomaticPages && filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage)) == true;
        reviewQueued.Enabled = !busy && (issue || cached || selected?.Output?.FiledPath is not null);
        if (stopPreparation.Parent is { } batchActions)
            batchActions.Visible = activeScreen != Screen.Decisions;
        openQueued.Visible = openDestination.Visible = activeScreen != Screen.Decisions;
        reviewNext.Enabled = !busy && filingQueue.Batches.Any(item => item.NeedsReview);
        discardQueued.Enabled = !busy && issue && reviewing != (batch!.Id, selected!.Number);
        openQueued.Enabled = !busy && selected?.Output?.FiledPath is not null;
        openDestination.Enabled = !busy && batch is not null;
        var canDeliver = !busy && batch?.HasPending == true && transferring != batch.Id;
        var paused = batch?.Phase == BatchPhase.Paused;
        retryDelivery.Text = paused ? "Resume processing" : "Retry delivery";
        retryDelivery.Enabled = canDeliver || (!busy && paused);
        redirectDelivery.Enabled = canDeliver;
        stopPreparation.Enabled = !busy && preparing is { } active && active.Id == batch?.Id &&
            !active.Stop.IsCancellationRequested;
        removeBatch.Enabled = !busy && batch is not null && CanRemoveBatch(batch);
        clearFinished.Enabled = !busy && filingQueue.Batches.Any(batch => batch.IsFinished && CanRemoveBatch(batch));
        reprocess.Enabled = !busy && batch is not null && filingQueue.CanReprocess(batch);
    }

    private Task ReviewNext()
    {
        var next = filingQueue.Batches.Reverse().FirstOrDefault(batch => batch.NeedsReview);
        if (next is null) return Task.CompletedTask;
        RefreshQueue(next.Id);
        var issue = next.Documents.First(doc => doc.Disposition == QueueDisposition.Review);
        foreach (ListViewItem row in documentList.Items)
            if (row.Tag is DocumentRow selected && selected.Number == issue.Number)
            {
                row.Selected = true;
                row.EnsureVisible();
                break;
            }
        return ReviewDocument(next.Id, issue.Number);
    }

    private Task ReviewQueued() => SelectedBatch is { } id && SelectedDocument is { } selected
        ? ReviewDocument(id, selected.Number, selected.Output)
        : Task.CompletedTask;

    private Task ReviewDocument(Guid id, int number, QueueOutput? output = null) => Run("Opening document for review…", async () =>
    {
        var batch = filingQueue.Get(id);
        var doc = batch.Documents.Single(document => document.Number == number);
        if (reviewing == (id, number) && pages.Count > 0)
        {
            ShowScreen(Screen.Review);
            status.Text = "Continue reviewing this document.";
            return;
        }
        if (doc.FromAutomaticPages && filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage))
        {
            if (!LeaveCurrentReview()) return;
            await OpenAutomaticReview(batch, doc);
            return;
        }
        if (doc.Disposition == QueueDisposition.Ready && output?.FiledPath is { } path)
        {
            if (!LeaveCurrentReview()) return;
            if (IsPdfPath(path) && await Task.Run(() => File.Exists(path)))
            {
                StartQueueReview(batch, documentNumber: null, oneDocument: true);
                try
                {
                    await AddPages(engine.Import(path));
                }
                catch (IOException) when (pages.Count == 0 &&
                    filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage))
                {
                    await OpenOriginals(batch, doc.FirstPage, doc.LastPage, oneDocument: true);
                    status.Text = "The filed PDF is unavailable. Retained original sides are open. Use Save copy to export.";
                    return;
                }
                if (pages.Count == 0) throw new IOException("The filed PDF contains no reviewable pages.");
                dirty = false;
                await PrepareBatch();
                status.Text = "Filed PDF opened. Use Save copy to export your edits.";
            }
            else if (filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage))
            {
                await OpenOriginals(batch, doc.FirstPage, doc.LastPage, oneDocument: true);
                status.Text = "The filed PDF is unavailable. Retained original sides are open. Use Save copy to export.";
            }
            else
                throw new IOException("The filed PDF and its local originals are unavailable. Use Open PDF or images if the file was moved.");
            return;
        }
        if (doc.Disposition != QueueDisposition.Review) return;
        if (!LeaveCurrentReview()) return;
        StartQueueReview(batch, doc.Number, !batch.Filing.DetectDocuments);
        await AddPages(ReadReviewPages(id, doc.FirstPage, doc.LastPage, doc.ReviewOmissions));
        dirty = false;
        await PrepareBatch();
        status.Text = doc.Reason ?? "Check these pages, then approve filing.";
    });

    private async Task OpenAutomaticReview(QueueBatch batch, QueueDocument doc)
    {
        var needsDecision = doc.Disposition == QueueDisposition.Review;
        StartQueueReview(batch, needsDecision ? doc.Number : null, !needsDecision || !batch.Filing.DetectDocuments);
        automaticReview = (batch.Id, doc);
        var numbers = Enumerable.Range(doc.FirstPage, doc.LastPage - doc.FirstPage + 1)
            .Except(doc.ReviewOmissions ?? []).ToArray();
        var focus = doc.DecisionPage is { } decision && numbers.Contains(decision) ? decision : numbers[0];
        await AddAutomaticPage(batch, focus, useCache: true);
        UpdatePlan();
        await RefreshPreview();
        status.Text = doc.Reason ?? "Opened local scan. Use Save copy to export edits.";
        foreach (var number in numbers.Where(number => number != focus))
            await AddAutomaticPage(batch, number, useCache: true);
        pages.Sort((left, right) => left.CaptureNumber.CompareTo(right.CaptureNumber));
        current = Array.IndexOf(numbers, focus);
        var focused = pages[current];
        await PrepareBatch(refreshPreview: focused.Prepared is null);
        status.Text = doc.Reason ?? "Opened local scan. Use Save copy to export edits.";
    }

    private async Task AddAutomaticPage(QueueBatch batch, int number, bool useCache)
    {
        var token = operation!.Token;
        token.ThrowIfCancellationRequested();
        var cached = useCache ? filingQueue.ReadPrepared(batch.Id, number, allowOlder: true) : null;
        var side = !batch.Duplex ? SheetSide.Unpaired :
            number % 2 == 0 && filingQueue.ReadPrepared(batch.Id, number - 1, allowOlder: true)?.OmitSide != true
                ? SheetSide.Back : SheetSide.Front;
        await foreach (var image in ReadReviewPages(batch.Id, number, number))
        {
            pages.Add(new Page(image, side, batch.Duplex ? (number + 1) / 2 : number)
            {
                CaptureNumber = number,
                Prepared = cached is { OmitSide: false } ? cached : null,
                Options = cached is { OmitSide: false } ? batch.Processing : null
            });
            break;
        }
        if (pages.All(page => page.CaptureNumber != number))
            throw new IOException($"Original side {number} could not be opened.");
    }

    private int[] AvailableOmissions() => automaticReview is { } active
        ? (active.Document.ReviewOmissions ?? []).Except(pages.Select(page => page.CaptureNumber)).ToArray() : [];

    private Task RestoreOmittedSide() => Run("Opening an omitted side…", async () =>
    {
        if (automaticReview is not { } active) return;
        var omitted = AvailableOmissions();
        if (omitted.Length == 0) return;
        var batch = filingQueue.Get(active.Id);
        using var dialog = new Form
        {
            Text = "Restore an omitted side", ClientSize = new Size(690, 530),
            StartPosition = FormStartPosition.CenterParent, Font = Font, ShowInTaskbar = false
        };
        using var choices = new ListBox { Dock = DockStyle.Left, Width = 155, AccessibleName = "Omitted scanner sides" };
        using var image = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
        using var actions = Row();
        actions.Dock = DockStyle.Bottom;
        var add = new Button { Text = "Restore selected side", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([add, close]);
        dialog.Controls.Add(image);
        dialog.Controls.Add(choices);
        dialog.Controls.Add(actions);
        dialog.AcceptButton = add;
        dialog.CancelButton = close;
        choices.Items.AddRange(omitted.Select(number => $"Original side {number}").ToArray());
        choices.SelectedIndexChanged += (_, _) =>
        {
            using var raw = Image.FromFile(filingQueue.Original(active.Id, omitted[choices.SelectedIndex]));
            SetImage(image, new Bitmap(raw));
        };
        choices.SelectedIndex = 0;
        int selected;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            selected = omitted[choices.SelectedIndex];
        }
        finally { SetImage(image, null); }
        await AddAutomaticPage(batch, selected, useCache: false);
        pages.Sort((left, right) => left.CaptureNumber.CompareTo(right.CaptureNumber));
        current = pages.FindIndex(page => page.CaptureNumber == selected);
        dirty = true;
        await PrepareBatch();
        status.Text = $"Restored original side {selected}. Save to include it.";
    });

    private Task ReprocessOriginals() => Run("Opening retained original pages…", async () =>
    {
        if (SelectedBatch is not { } id || !LeaveCurrentReview()) return;
        var batch = filingQueue.Get(id);
        if (!filingQueue.CanReprocess(batch))
            throw new IOException("The local originals are no longer available.");
        await OpenOriginals(batch, 1, batch.Pages, !batch.Filing.DetectDocuments);
        status.Text = "Original sides reopened. Use Save copy to choose a destination for the new PDF.";
    });

    private async Task OpenOriginals(QueueBatch batch, int first, int last, bool oneDocument)
    {
        StartQueueReview(batch, documentNumber: null, oneDocument);
        await AddPages(ReadReviewPages(batch.Id, first, last), batch.Duplex && first % 2 == 1);
        dirty = false;
        await PrepareBatch();
    }

    private void StartQueueReview(QueueBatch batch, int? documentNumber, bool oneDocument)
    {
        DisposePages();
        if (documentNumber is { } number)
            reviewing = (batch.Id, number);
        else
            reprocessing = batch.Id;
        oneDocumentBatch = oneDocument;
        language.SelectedIndex = batch.Processing.Language switch { "eng" => 1, "deu+eng" => 2, _ => 0 };
        straighten.Checked = batch.Processing.Straighten;
        clean.Checked = batch.Processing.Clean;
        autoOrient.Checked = batch.Processing.AutoOrient;
        ShowScreen(Screen.Review);
    }

    private async IAsyncEnumerable<NAPS2.Images.ProcessedImage> ReadReviewPages(Guid id, int first, int last,
        int[]? omitted = null)
    {
        for (var page = first; page <= last; page++)
        {
            if (omitted?.Contains(page) == true) continue;
            await foreach (var image in engine.Import(filingQueue.Original(id, page)))
                yield return image;
        }
    }

    private bool LeaveCurrentReview()
    {
        if (pages.Count == 0 || !dirty) return true;
        return Confirm(reviewing is null
            ? "Replace the current unsaved review batch?"
            : "Close this review and discard your edits? The original pages will stay in the queue.");
    }

    private Task ShowNeighbors()
    {
        if (reviewing is not { } current) return Task.CompletedTask;
        var batch = filingQueue.Get(current.Batch);
        var document = batch.Documents.Single(doc => doc.Number == current.Document);
        var compareBoundary = this.current > 0 && document.DecisionPage == pages[this.current].CaptureNumber &&
            document.DecisionPage is not null && document.Reason?.StartsWith("Small item", StringComparison.Ordinal) != true;
        var pair = compareBoundary
            ? new[] { (pages[this.current - 1].CaptureNumber, 0, "Previous page"),
                (pages[this.current].CaptureNumber, 1, "Possible new document") }
            : new[] { (document.FirstPage - 1, 0, "Previous page"), (document.LastPage + 1, 1, "Next page") };
        using var dialog = new Form
        {
            Text = "Neighboring scanned pages", ClientSize = new Size(860, 615), StartPosition = FormStartPosition.CenterParent,
            Font = Font, ShowInTaskbar = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var images = new List<Image>();
        try
        {
            foreach (var (index, column, name) in pair)
            {
                if (index < 1 || index > batch.Pages)
                {
                    layout.Controls.Add(Caption(column == 0 ? "Beginning of batch" : "End of batch"), column, 0);
                    continue;
                }
                using var source = Image.FromFile(filingQueue.Original(batch.Id, index));
                var copy = new Bitmap(source);
                images.Add(copy);
                layout.Controls.Add(PreviewPane($"{name} · {index}", new PictureBox
                    { SizeMode = PictureBoxSizeMode.Zoom, Image = copy }), column, 0);
            }
            dialog.Controls.Add(layout);
            if (compareBoundary)
            {
                var decisions = Row();
                decisions.Dock = DockStyle.Bottom;
                decisions.Controls.AddRange([
                    new Button { Text = "Split here", AutoSize = true, DialogResult = DialogResult.Yes },
                    new Button { Text = "Keep together", AutoSize = true, DialogResult = DialogResult.No },
                    new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel }
                ]);
                dialog.Controls.Add(decisions);
            }
            var result = dialog.ShowDialog(this);
            if (result is DialogResult.Yes or DialogResult.No)
                SetBoundary(result == DialogResult.Yes);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
        return Task.CompletedTask;
    }

    private Task OpenQueued() => Run("Opening the filed PDF…", async () =>
    {
        if (SelectedDocument?.Output?.FiledPath is not { } file) return;
        if (!IsPdfPath(file) || !await Task.Run(() => File.Exists(file)))
            throw new IOException("The filed PDF is unavailable. Check the folder or whether a downstream app moved it.");
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        status.Text = "Opened the filed PDF.";
    });

    private static bool IsPdfPath(string path) => Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    private Task OpenQueuedFolder() => Run("Opening the destination…", async () =>
    {
        if (SelectedBatch is not { } id) return;
        var folder = SelectedDocument?.Output?.FiledPath is { } file
            ? Path.GetDirectoryName(file)! : filingQueue.Get(id).Destination;
        if (!await Task.Run(() => Directory.Exists(folder))) throw new IOException("The destination is currently unavailable.");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        status.Text = "Opened the destination folder.";
    });

    private Task RetryQueued() => Run("Scheduling queue work…", () =>
    {
        if (SelectedBatch is { } id && transferring != id)
        {
            if (filingQueue.Get(id).Phase == BatchPhase.Paused)
            {
                filingQueue.Resume(id);
                status.Text = "Processing scheduled.";
            }
            else
            {
                filingQueue.Retry(id);
                status.Text = "Delivery retry scheduled.";
            }
            RefreshQueue(id);
            PumpQueue();
        }
        return Task.CompletedTask;
    });

    private Task StopQueued()
    {
        if (preparing is { } active && SelectedBatch == active.Id)
        {
            filingQueue.PauseQueued(active.Id);
            active.Stop.Cancel();
            RefreshQueue(active.Id);
        }
        return Task.CompletedTask;
    }

    private bool CanRemoveBatch(QueueBatch batch) =>
        batch.Phase is not (BatchPhase.Capturing or BatchPhase.Processing) &&
        preparing?.Id != batch.Id && transferring != batch.Id && choosingNewDestination != batch.Id && reviewing?.Batch != batch.Id &&
        reprocessing != batch.Id;

    private Task RemoveQueuedBatch() => Run("Checking the selected batch…", () =>
    {
        if (SelectedBatch is not { } id || !CanRemoveBatch(filingQueue.Get(id))) return Task.CompletedTask;
        var batch = filingQueue.Get(id);
        var prompt = batch.IsFinished
            ? "Remove this batch and any retained local originals? Filed PDFs will stay in the save folder."
            : "Remove this batch and delete its unfiled local scans? Any already-filed PDFs will stay in the save folder.";
        if (!Confirm(prompt)) return Task.CompletedTask;
        if (!CanRemoveBatch(filingQueue.Get(id)))
            throw new InvalidOperationException("This batch just started working. Wait for it to finish or stop processing first.");
        var cleaned = filingQueue.RemoveBatch(id);
        RefreshQueue();
        status.Text = cleaned ? "Batch removed." : "Batch removed. Local file cleanup will retry when the app restarts.";
        return Task.CompletedTask;
    });

    private Task ClearFinishedBatches() => Run("Checking finished batches…", () =>
    {
        var finished = filingQueue.Batches.Where(batch => batch.IsFinished && CanRemoveBatch(batch)).ToArray();
        if (finished.Length == 0 ||
            !Confirm($"Clear {finished.Length} finished or empty batch(es) and their retained local originals? Filed PDFs will stay in the save folder."))
            return Task.CompletedTask;
        var cleaned = true;
        try
        {
            foreach (var batch in finished)
                cleaned &= filingQueue.RemoveBatch(batch.Id);
        }
        finally
        {
            RefreshQueue();
        }
        status.Text = cleaned ? "Finished batches cleared." : "History cleared. Local file cleanup will retry when the app restarts.";
        return Task.CompletedTask;
    });

    private Task RedirectQueued() => Run("Choosing a destination for pending PDFs…", async () =>
    {
        if (SelectedBatch is not { } id || transferring == id) return;
        choosingNewDestination = id;
        try
        {
            var batch = filingQueue.Get(id);
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a folder for this batch’s pending PDFs", UseDescriptionForTitle = true,
                InitialDirectory = await Task.Run(() => Directory.Exists(batch.Destination)
                    ? batch.Destination : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                filingQueue.Redirect(id, dialog.SelectedPath);
                status.Text = "Pending PDFs will be delivered to the new folder.";
            }
            else status.Text = "Folder change canceled.";
        }
        finally
        {
            choosingNewDestination = null;
            RefreshQueue(id);
            PumpQueue();
        }
    });

    private Task DiscardQueued() => Run("Checking the selected document…", () =>
    {
        if (SelectedBatch is { } id && SelectedDocument is { } document && reviewing != (id, document.Number) &&
            Confirm("Discard these queued pages? They will not be filed."))
        {
            filingQueue.Discard(id, document.Number);
            RefreshQueue(id);
            status.Text = "The selected document was discarded.";
        }
        else status.Text = "Discard canceled.";
        return Task.CompletedTask;
    });
}
