using System.Diagnostics;
using System.Threading.Channels;
using NAPS2.Scan;

namespace PaperQuay;

internal sealed partial class Workbench
{
    private readonly FilingQueue filingQueue;
    private readonly System.Windows.Forms.Timer queueTimer = new() { Interval = 3000 };
    private readonly CancellationTokenSource queueLifetime = new();
    private readonly ListView batchList = QueueList("Scan batches", ("Scanned", 111), ("Sides", 47), ("Status", 112));
    private readonly ListView documentList = QueueList("Documents in selected batch", ("Doc", 46),
        ("Pages", 53), ("Status", 112), ("File or review reason", 250));
    private readonly Label emptyBatches = EmptyList();
    private readonly Label emptyDocuments = EmptyList();
    private readonly Label batchDetails = Caption("Scanned batches will appear here.");
    private Button reviewQueued = null!;
    private Button reviewNext = null!;
    private Button openQueued = null!;
    private Button openDestination = null!;
    private Button clearHistory = null!;
    private ToolStripMenuItem retryDelivery = null!;
    private ToolStripMenuItem redirectDelivery = null!;
    private Button discardQueued = null!;
    private ToolStripMenuItem stopPreparation = null!;
    private ToolStripMenuItem removeBatch = null!;
    private ToolStripMenuItem clearFinished = null!;
    private ToolStripMenuItem reprocess = null!;
    private Button batchMenuButton = null!;
    private bool updatingQueue;
    private bool clearingHistory;
    private Guid? lastQueuedSelection;
    private (Guid Id, CancellationTokenSource Stop)? preparing;
    private int preparedPages;
    private long preparationStarted;
    private string preparationStage = "Recognizing";
    private readonly HashSet<Guid> transferring = [];
    private Guid? choosingNewDestination;
    private (Guid Batch, int Document)? reviewing;
    private Guid? reprocessing;
    private (Guid Id, QueueDocument Document)? automaticReview;
    private Button? restoreSides;
    private bool ShowingQueue => activeScreen is Screen.Queue or Screen.History or Screen.Decisions;

    private sealed record DocumentRow(int Number, QueueOutput? Output);
    private Guid? SelectedBatch => batchList.SelectedItems.Count == 1 && batchList.SelectedItems[0].Tag is Guid id ? id : null;
    private DocumentRow? SelectedDocument => documentList.SelectedItems.Count == 1
        ? documentList.SelectedItems[0].Tag as DocumentRow : null;

    private static ListView QueueList(string name, params (string Name, int Width)[] columns)
    {
        var view = new ListView
        {
            AccessibleName = name, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Color.White, ForeColor = Ink
        };
        foreach (var (title, width) in columns) view.Columns.Add(title, width);
        var fixedWidth = columns.Take(columns.Length - 1).Sum(column => column.Width);
        view.ClientSizeChanged += (_, _) => view.Columns[^1].Width =
            Math.Max(112, view.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth);
        return view;
    }

    private Control BuildQueueView()
    {
        reviewQueued = Action("Review document", ReviewQueued, primary: true);
        reviewNext = Action("Review next", ReviewNext);
        openQueued = Action("Open PDF", OpenQueued);
        openDestination = Action("Open folder", OpenQueuedFolder);
        clearHistory = Action("Clear history", ClearHistory);
        retryDelivery = BatchAction("Retry delivery", RetryQueued);
        redirectDelivery = BatchAction("Change destination…", RedirectQueued);
        discardQueued = Action("Discard document…", DiscardQueued);
        stopPreparation = BatchAction("Stop processing", StopQueued);
        removeBatch = BatchAction("Remove batch…", RemoveQueuedBatch);
        clearFinished = BatchAction("Delete finished batch records…", ClearFinishedBatches);
        reprocess = BatchAction("Reprocess originals", ReprocessOriginals);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([stopPreparation, retryDelivery, redirectDelivery, reprocess, new ToolStripSeparator(),
            removeBatch, clearFinished]);
        Disposed += (_, _) => menu.Dispose();
        batchMenuButton = Action("Batch actions…", () =>
        {
            menu.Show(batchMenuButton, new Point(0, batchMenuButton.Height));
            return Task.CompletedTask;
        });
        var commands = Row();
        commands.Controls.AddRange([reviewQueued, reviewNext, openQueued, openDestination, discardQueued, batchMenuButton, clearHistory]);

        var lists = SideBySide(39);
        lists.AutoSize = false;
        var batches = Card("Batches");
        batches.AutoSize = false;
        AddRow(batches, ListSurface(batchList, emptyBatches), fill: true);
        var documents = Card("Documents");
        documents.AutoSize = false;
        documents.Margin = Padding.Empty;
        AddRow(documents, ListSurface(documentList, emptyDocuments), fill: true);
        lists.Controls.Add(batches, 0, 0);
        lists.Controls.Add(documents, 1, 0);

        var body = Column();
        body.AutoSize = false;
        AddRow(body, commands);
        AddRow(body, lists, fill: true);
        batchDetails.AutoSize = false;
        batchDetails.AutoEllipsis = true;
        batchDetails.Dock = DockStyle.Fill;
        batchDetails.Height = 34;
        batchDetails.TextAlign = ContentAlignment.MiddleLeft;
        batchDetails.ForeColor = Muted;
        AddRow(body, batchDetails);
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

    private static Label EmptyList() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Muted,
        TextAlign = ContentAlignment.MiddleCenter
    };

    private static Panel ListSurface(ListView list, Label empty)
    {
        var surface = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        surface.Controls.Add(list);
        surface.Controls.Add(empty);
        empty.BringToFront();
        return surface;
    }

    private static ToolStripMenuItem BatchAction(string title, Func<Task> action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += async (_, _) => await action();
        return item;
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
        if (filingQueue.RetryPreparationSaves()) RefreshQueue();
        filingQueue.PruneExpiredOriginals(reprocessing);
        if (preparing is null && filingQueue.NextPreparation() is { } pending)
            _ = PrepareQueued(pending.Id);
        while (transferring.Count < 2 && filingQueue.NextTransfer(transferring, choosingNewDestination) is { } ready)
        {
            _ = TransferQueued(ready.Id);
            if (!transferring.Contains(ready.Id)) break;
        }
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
        transferring.Add(id);
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
            transferring.Remove(id);
            if (!IsDisposed)
            {
                RefreshQueue();
                if (advanced) BeginInvoke(PumpQueue);
            }
        }
    }

    private Task QuickScan() => Run("Connecting to the scanner…", async () =>
    {
        var device = await SelectScanner();
        var duplex = sides.SelectedIndex == 0;
        var dpi = ScanDpi;
        var paper = SelectedPaper;
        var bitDepth = SelectedBitDepth;
        var token = operation!.Token;
        token.ThrowIfCancellationRequested();
        var batch = filingQueue.Begin(saveFolder, duplex, GetScanProcessingOptions(), GetQuickChoices(), workers.SelectedIndex + 1);
        var early = preparing is null && filingQueue.NextPreparation() is null ? Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions { SingleWriter = true }) : null;
        if (early is not null) _ = PrepareCapturedSheets(batch, early.Reader);
        RefreshQueue();
        var count = 0;
        string? failure = null;
        status.Text = $"Capturing {paper.Name} pages for the local queue…";
        try
        {
            await foreach (var page in CapturePages(device, duplex, dpi, paper, bitDepth, token))
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
        catch (FeederStoppedException ex)
        {
            failure = ex.Message;
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
        status.Text = filingQueue.IsSavingPreparation(batch.Id) ? "The scanned pages are stored locally. Waiting to save the queue record." :
            completed.Pages == 0 ? failure ?? "The scanner returned no pages." :
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
            var visible = activeScreen switch
            {
                Screen.Decisions => items.Where(batch => batch.NeedsReview),
                Screen.Queue => items.Where(batch => !batch.IsFinished),
                _ => filingQueue.History
            };
            foreach (var batch in visible)
            {
                var state = BatchStatus(batch);
                var row = new ListViewItem([batch.Created.LocalDateTime.ToString("dd MMM HH:mm"),
                    batch.Pages == 0 ? "—" : batch.Pages.ToString(), state])
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
        emptyBatches.Visible = batchList.Items.Count == 0;
        emptyBatches.Text = activeScreen switch
        {
            Screen.Decisions => "No scans need a decision.",
            Screen.Queue => "No pending batches.",
            _ => "No scans in history."
        };
        RefreshDocuments();
    }

    private string BatchStatus(QueueBatch batch) => batch.Phase switch
    {
        _ when filingQueue.IsSavingPreparation(batch.Id) => "Waiting to save queue",
        BatchPhase.Capturing when preparing?.Id == batch.Id && preparedPages > 0 => $"Scanning · {preparedPages} ready",
        BatchPhase.Capturing => "Scanning",
        BatchPhase.Queued when preparing?.Id == batch.Id && preparing?.Stop.IsCancellationRequested == false
            => $"Recognizing {preparedPages}/{batch.Pages}",
        BatchPhase.Queued => "Queued",
        BatchPhase.Processing when preparing?.Stop.IsCancellationRequested == true => "Stopping…",
        BatchPhase.Processing => preparationStage == "Building PDFs" ? preparationStage : $"Recognizing {preparedPages}/{batch.Pages}",
        BatchPhase.Paused => "Paused",
        BatchPhase.Empty => "No pages",
        _ when transferring.Contains(batch.Id) => "Filing",
        _ when batch.NeedsReview && batch.TransferError is not null => "Review + waiting",
        _ when batch.NeedsReview => "Needs a decision",
        _ when batch.TransferError is not null => "Waiting for folder",
        _ when batch.HasPending => "Ready to file",
        _ when batch.FiledCount > 0 => "Filed",
        _ when batch.Documents.Any(doc => doc.Disposition == QueueDisposition.SkippedBlank) => "Empty pages skipped",
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
                    (omitted == 0 ? batch.Destination : $"{batch.Destination} · {omitted} empty or show-through page(s) skipped");
                foreach (var doc in batch.Documents.Where(doc => activeScreen != Screen.Decisions ||
                    doc.Disposition == QueueDisposition.Review))
                {
                    var range = doc.FirstPage == doc.LastPage ? doc.FirstPage.ToString() : $"{doc.FirstPage}–{doc.LastPage}";
                    if (doc.Outputs is { Length: > 0 })
                    {
                        foreach (var file in doc.Outputs)
                        {
                            var outcome = file.FiledPath is not null ? "Filed" :
                                batch.TransferError is not null ? "Waiting for folder" :
                                transferring.Contains(id) ? "Filing" : "Ready";
                            var details = doc.WeakOcrPages == 0 ? file.Name :
                                $"Weak OCR on {doc.WeakOcrPages} page(s) · {file.Name}";
                            AddDocumentRow(doc.Number, range, outcome, details, file, previous);
                        }
                    }
                    else
                    {
                        var disposition = doc.Disposition switch
                        {
                            QueueDisposition.Review => "Needs a decision",
                            QueueDisposition.SkippedBlank => "Skipped",
                            QueueDisposition.Ready => "Deleted",
                            _ => "Discarded"
                        };
                        AddDocumentRow(doc.Number, range, disposition, doc.Reason ?? "", null, previous);
                    }
                }
                if (documentList.SelectedItems.Count == 0 && documentList.Items.Count > 0)
                    documentList.Items[0].Selected = true;
            }
            else batchDetails.Text = "";
        }
        finally
        {
            documentList.EndUpdate();
            updatingQueue = false;
        }
        emptyDocuments.Visible = documentList.Items.Count == 0;
        emptyDocuments.Text = SelectedBatch is null ? "Select a batch to see its documents." : "No documents in this batch.";
        batchDetails.Visible = SelectedBatch is not null;
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
        var inDecisions = activeScreen == Screen.Decisions;
        clearHistory.Visible = activeScreen == Screen.History;
        clearHistory.Enabled = !clearingHistory;
        openQueued.Visible = openDestination.Visible = batchMenuButton.Visible = !inDecisions;
        reviewNext.Visible = inDecisions;
        reviewNext.Enabled = !busy && filingQueue.Batches.Any(item => item.NeedsReview);
        discardQueued.Enabled = !busy && issue && CanDiscardReview(batch!.Id, selected!.Number);
        discardQueued.Visible = issue;
        openQueued.Enabled = !busy && selected?.Output?.FiledPath is not null;
        openDestination.Enabled = !busy && batch is not null;
        var canDeliver = !busy && batch?.HasPending == true && !transferring.Contains(batch.Id);
        var paused = batch?.Phase == BatchPhase.Paused;
        var saving = batch is not null && filingQueue.IsSavingPreparation(batch.Id);
        retryDelivery.Text = saving ? "Retry queue save" : paused ? "Resume processing" : "Retry delivery";
        retryDelivery.Enabled = canDeliver || (!busy && (paused || saving));
        redirectDelivery.Enabled = canDeliver || (!busy && batch is { Phase: BatchPhase.Planned, NeedsReview: true } &&
            !transferring.Contains(batch.Id));
        stopPreparation.Enabled = !busy && preparing is { } active && active.Id == batch?.Id &&
            !active.Stop.IsCancellationRequested;
        removeBatch.Enabled = !busy && batch is not null && CanRemoveBatch(batch);
        clearFinished.Enabled = !busy && filingQueue.Batches.Any(batch => batch.IsFinished && CanRemoveBatch(batch));
        reprocess.Enabled = !busy && batch is not null && filingQueue.CanReprocess(batch);
        batchMenuButton.Enabled = stopPreparation.Enabled || retryDelivery.Enabled || redirectDelivery.Enabled ||
            removeBatch.Enabled || clearFinished.Enabled || reprocess.Enabled;
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
        if (doc.Disposition == QueueDisposition.Review ||
            (doc.FromAutomaticPages && filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage)))
        {
            if (!LeaveCurrentReview()) return;
            await OpenAutomaticReview(batch, doc);
            return;
        }
        if (doc.Disposition == QueueDisposition.Ready && output?.FiledPath is not null)
        {
            if (!LeaveCurrentReview()) return;
            string snapshot;
            try
            {
                snapshot = await Task.Run(() => engine.SnapshotFiledPdf(output, operation!.Token));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await RestoreFiledOutput(batch, doc, output);
                return;
            }
            try
            {
                StartQueueReview(batch, documentNumber: null, SingleDocument(batch.Filing));
                await AddPages(engine.Import(snapshot));
                if (pages.Count == 0) throw new IOException("The filed PDF contains no reviewable pages.");
                dirty = false;
                await PrepareBatch();
                status.Text = "Filed PDF opened. Use Save copy to export your edits.";
            }
            catch (IOException) when (pages.Count == 0)
            {
                await RestoreFiledOutput(batch, doc, output);
            }
            finally { DocumentEngine.DeleteScratchFile(snapshot); }
        }
    });

    private async Task RestoreFiledOutput(QueueBatch batch, QueueDocument doc, QueueOutput output)
    {
        if (output.Source is { Pages.Length: > 0 } source && source.Pages.All(edit =>
                filingQueue.HasOriginals(batch, edit.Source, edit.Source)))
        {
            StartQueueReview(batch, documentNumber: null, SingleDocument(batch.Filing));
            ApplyReviewProcessing(source.Processing);
            foreach (var edit in source.Pages)
                await AddAutomaticPage(batch, edit.Source, useCache: true, edit: edit);
            await PrepareBatch();
        }
        else if (output.Source is null && doc.Outputs is { Length: 1 } &&
            !output.Name.Contains($" - {doc.Number:D2}-", StringComparison.Ordinal) &&
            filingQueue.HasOriginals(batch, doc.FirstPage, doc.LastPage))
            await OpenOriginals(batch, doc.FirstPage, doc.LastPage, SingleDocument(batch.Filing));
        else
            throw new IOException("The selected PDF and its exact source pages are unavailable. Reprocess originals opens the original batch.");
        status.Text = "The filed PDF is unavailable. Retained original sides are open. Use Save copy to export.";
    }

    private async Task OpenAutomaticReview(QueueBatch batch, QueueDocument doc)
    {
        var needsDecision = doc.Disposition == QueueDisposition.Review;
        var choices = !needsDecision ? SingleDocument(batch.Filing) :
            doc.FromAutomaticPages ? batch.Filing with { SheetsPerDocument = 0 } : batch.Filing;
        StartQueueReview(batch, needsDecision ? doc.Number : null, choices);
        automaticReview = (batch.Id, doc);
        if (doc.Draft is { } saved) ApplyReviewProcessing(saved.Processing);
        var edits = doc.Draft?.Pages ?? Enumerable.Range(doc.FirstPage, doc.LastPage - doc.FirstPage + 1)
            .Except(doc.ReviewOmissions ?? []).Select(number => new PageEdit(number)).ToArray();
        foreach (var edit in edits)
            await AddAutomaticPage(batch, edit.Source, useCache: true, edit: edit);
        current = Math.Max(0, Array.FindIndex(edits, edit => edit.Source == doc.DecisionPage));
        var boundary = current > 0 && doc.DecisionPage is not null &&
            doc.Reason?.StartsWith("Small item", StringComparison.Ordinal) != true &&
            doc.Reason?.StartsWith("Possible writing", StringComparison.Ordinal) != true;
        if (boundary) previewMode.SelectedIndex = BoundaryView;
        await PrepareBatch();
        status.Text = doc.Reason ?? "Opened local scan. Use Save copy to export edits.";
    }

    private async Task AddAutomaticPage(QueueBatch batch, int number, bool useCache, bool restore = false, PageEdit? edit = null)
    {
        var token = operation!.Token;
        token.ThrowIfCancellationRequested();
        var cached = useCache ? filingQueue.ReadPrepared(batch.Id, number) : null;
        var prepared = edit is { Rotation: not null } or { Crop: true } ? null : cached;
        var layout = filingQueue.PageLayout(batch, number);
        await foreach (var image in engine.Import(filingQueue.Original(batch.Id, number)))
        {
            pages.Add(new Page(image, layout.Side, layout.Sheet)
            {
                CaptureNumber = number,
                KeepBlank = restore || edit?.KeepBlank == true,
                Exclude = edit?.Exclude == true,
                BreakBefore = edit?.BreakBefore,
                Rotation = edit?.Rotation,
                Crop = edit?.Crop == true,
                Prepared = prepared,
                Options = prepared is not null ? batch.Processing : null,
                Evidence = cached switch
                {
                    null => null,
                    { ReviewSide: true } => ReverseEvidence.Ambiguous,
                    { VerifiedBlank: true } => ReverseEvidence.Empty,
                    { OmitSide: true } => ReverseEvidence.ShowThrough,
                    _ => ReverseEvidence.Content
                }
            });
            return;
        }
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
        await AddAutomaticPage(batch, selected, useCache: false, restore: true);
        var restored = pages[^1];
        pages.RemoveAt(pages.Count - 1);
        current = pages.FindIndex(page => page.CaptureNumber > selected);
        if (current < 0) current = pages.Count;
        pages.Insert(current, restored);
        RememberReviewEdits();
        await PrepareBatch();
        status.Text = $"Restored original side {selected}. Save to include it.";
    });

    private Task ReprocessOriginals() => Run("Opening retained original pages…", async () =>
    {
        if (SelectedBatch is not { } id || !LeaveCurrentReview()) return;
        var batch = filingQueue.Get(id);
        if (!filingQueue.CanReprocess(batch))
            throw new IOException("The local originals are no longer available.");
        await OpenOriginals(batch, 1, batch.Pages, GetQuickChoices());
        status.Text = "Original sides reopened. Use Save copy to choose a destination for the new PDF.";
    });

    private async Task OpenOriginals(QueueBatch batch, int first, int last, QuickFilingOptions choices)
    {
        StartQueueReview(batch, documentNumber: null, choices);
        for (var number = first; number <= last; number++)
            await AddAutomaticPage(batch, number, useCache: false);
        await PrepareBatch();
    }

    private static QuickFilingOptions SingleDocument(QuickFilingOptions options) =>
        options with { DetectDocuments = false, SheetsPerDocument = 0 };

    private void StartQueueReview(QueueBatch batch, int? documentNumber, QuickFilingOptions choices)
    {
        DisposePages();
        if (documentNumber is { } number)
            reviewing = (batch.Id, number);
        else
            reprocessing = batch.Id;
        SetReviewGrouping(choices);
        ApplyReviewProcessing(batch.Processing);
        UpdatePlan();
        ShowScreen(Screen.Review);
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
        var pair = new[] { (document.FirstPage - 1, 0, "Previous page"), (document.LastPage + 1, 1, "Next page") };
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
            dialog.ShowDialog(this);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
        return Task.CompletedTask;
    }

    private Task OpenQueued() => Run("Opening the filed PDF…", async () =>
    {
        if (SelectedDocument?.Output is not { FiledPath: not null } file) return;
        var snapshot = await Task.Run(() => engine.SnapshotFiledPdf(file, operation!.Token));
        Process.Start(new ProcessStartInfo(snapshot) { UseShellExecute = true });
        status.Text = "Opened a verified copy of the filed PDF.";
    });

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
        if (SelectedBatch is { } id && !transferring.Contains(id))
        {
            if (filingQueue.IsSavingPreparation(id))
                status.Text = filingQueue.RetryPreparationSaves(id) ? "Saved queue status." : "Waiting to save queue status.";
            else if (filingQueue.Get(id).Phase == BatchPhase.Paused)
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
        !clearingHistory && batch.Phase is not (BatchPhase.Capturing or BatchPhase.Processing) &&
        preparing?.Id != batch.Id && !transferring.Contains(batch.Id) && choosingNewDestination != batch.Id && reviewing?.Batch != batch.Id &&
        reprocessing != batch.Id;

    private async Task ClearHistory()
    {
        if (clearingHistory) return;
        var history = filingQueue.History.ToArray();
        if (history.Length == 0)
        {
            status.Text = "History is already clear.";
            return;
        }
        var filed = history.SelectMany(batch => batch.Documents).SelectMany(doc => doc.Outputs ?? [])
            .Where(file => file.FiledPath is not null).ToArray();
        var paths = filed.Where(file => file.FiledHash is not null).Select(file => file.FiledPath!).ToArray();
        if (paths.Length > 0 && !ConfirmHistoryDeletion(paths, filed.Length - paths.Length)) return;

        clearingHistory = true;
        UpdateQueueActions();
        status.Text = "Clearing history…";
        try
        {
            var (failed, error, unverified) = await filingQueue.ClearHistory(history);
            status.Text = failed > 0 ? $"Could not clear {failed} file(s) or records. {error}" :
                filingQueue.History.Any() ? "Cleared the confirmed files. New scans are listed in History." :
                "History cleared. Pending batches are in Queue.";
            if (unverified > 0)
                status.Text += $" Kept {unverified} older PDF(s) without a recorded fingerprint.";
        }
        finally
        {
            clearingHistory = false;
            RefreshQueue();
        }
    }

    private bool ConfirmHistoryDeletion(string[] paths, int unverified)
    {
        using var dialog = new Form
        {
            Text = "Clear history", ClientSize = new Size(760, 320), MinimumSize = new Size(510, 250),
            StartPosition = FormStartPosition.CenterParent, Font = Font, ShowInTaskbar = false
        };
        var body = Column();
        body.AutoSize = false;
        body.Padding = new Padding(12);
        AddRow(body, Caption($"Permanently delete {paths.Length} saved PDF(s) and clear History? Pending scans will continue."));
        if (unverified > 0)
            AddRow(body, Caption($"{unverified} older PDF(s) have no recorded fingerprint and will be kept."));
        var files = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "PDFs to delete" };
        files.Items.AddRange(paths);
        AddRow(body, files, fill: true);
        var actions = Row();
        var delete = new Button { Text = "Delete PDFs and clear", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([delete, cancel]);
        AddRow(body, actions);
        dialog.Controls.Add(body);
        dialog.AcceptButton = dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

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
        status.Text = cleaned ? "Finished batch records deleted." : "Finished batch records removed. Local file cleanup will retry when the app restarts.";
        return Task.CompletedTask;
    });

    private Task RedirectQueued() => Run("Choosing a batch destination…", async () =>
    {
        if (SelectedBatch is not { } id || transferring.Contains(id)) return;
        choosingNewDestination = id;
        try
        {
            var batch = filingQueue.Get(id);
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a folder for this batch’s PDFs", UseDescriptionForTitle = true,
                InitialDirectory = await Task.Run(() => Directory.Exists(batch.Destination)
                    ? batch.Destination : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                await filingQueue.Redirect(id, dialog.SelectedPath, operation!.Token);
                status.Text = "Future PDFs will be delivered to the new folder.";
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

    private bool CanDiscardReview(Guid id, int number) => reviewing != (id, number) || pages.Count == 0;

    private Task DiscardQueued() => Run("Checking the selected document…", () =>
    {
        if (SelectedBatch is { } id && SelectedDocument is { } document && CanDiscardReview(id, document.Number) &&
            Confirm("Discard these queued pages? They will not be filed."))
        {
            filingQueue.Discard(id, document.Number);
            if (reviewing == (id, document.Number))
            {
                DisposePages();
                UpdatePlan();
            }
            RefreshQueue(id);
            status.Text = "The selected document was discarded.";
        }
        else status.Text = "Discard canceled.";
        return Task.CompletedTask;
    });
}
