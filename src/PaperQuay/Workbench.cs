using NAPS2.Images;
using NAPS2.Scan;

namespace PaperQuay;

internal sealed partial class Workbench : Form
{
    private static readonly string DefaultSaveFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private readonly DocumentEngine engine = new();
    private readonly List<Page> pages = [];
    private readonly ComboBox devices = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 208, AccessibleName = "Scanner connection",
        FormattingEnabled = true,
        Margin = new Padding(3, 7, 6, 3)
    };
    private readonly ComboBox sides = Options(128, "Both sides", "Front only");
    private readonly ComboBox resolution = Options(84, "300 dpi", "400 dpi", "600 dpi");
    private readonly ComboBox language = Options(145, "Deutsch", "English", "Deutsch + English");
    private readonly ComboBox workers = Options(58, "1", "2", "3", "4");
    private readonly ComboBox quickMode = Options(210, "One PDF per load", "Mixed stack");
    private readonly CheckBox skipBlankBacks = Check("Skip blank and show-through sides", true);
    private readonly CheckBox straighten = Check("Straighten", true);
    private readonly CheckBox autoOrient = Check("Auto-rotate", true);
    private readonly CheckBox clean = Check("Clean background", true);
    private readonly CheckBox detail = Check("100% zoom", false);
    private readonly PictureBox original = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly PictureBox corrected = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label pageNumber = Caption("No pages");
    private readonly Label status = Caption("Scan a batch or open an existing scan.");
    private readonly FlowLayoutPanel actions = Row();
    private readonly FlowLayoutPanel settings = Row();
    private readonly FlowLayoutPanel edits = Row();
    private readonly FlowLayoutPanel output = Row();
    private readonly Label destinationLabel = Caption("");
    private readonly Label groupLabel = Caption("No documents");
    private readonly ListView review = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill,
        HideSelection = false, AccessibleName = "Pages and suggested documents"
    };
    private readonly Button save;
    private readonly Button back;
    private readonly Button next;
    private readonly Button rotate;
    private readonly Button crop;
    private readonly Button remove;
    private readonly Button clear;
    private readonly Button split;
    private readonly Button analyze;
    private readonly Button cancel;
    private readonly Button ocrText;
    private readonly Button nextReview;
    private int current;
    private bool busy;
    private bool dirty;
    private bool updatingReview;
    private bool oneDocumentBatch;
    private int nextSheet;
    private CancellationTokenSource? operation;
    private GroupAssignment[] groups = [];
    private string saveFolder;

    private sealed class Page(ProcessedImage image, SheetSide side, int sheet) : IDisposable
    {
        public ProcessedImage Image { get; } = image;
        public SheetSide Side { get; } = side;
        public int Sheet { get; } = sheet;
        public int CaptureNumber { get; init; }
        public int? Rotation { get; set; }
        public bool Crop { get; set; }
        public bool? BreakBefore { get; set; }
        public PreparedPage? Prepared { get; set; }
        public ProcessingOptions? Options { get; set; }
        public void Dispose()
        {
            Image.Dispose();
            Prepared?.Dispose();
        }
    }

    public Workbench(FilingQueue queue, string? initialFile)
    {
        filingQueue = queue;
        Text = "PaperQuay 4.0.1";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(247, 249, 252);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1390, 840);
        MinimumSize = new Size(1110, 680);
        AutoScaleMode = AutoScaleMode.Dpi;
        sides.AccessibleName = "Feeder sides";
        resolution.AccessibleName = "Scan resolution";
        language.AccessibleName = "OCR language";
        workers.AccessibleName = "Parallel page workers";
        workers.SelectedIndex = Math.Clamp(Environment.ProcessorCount / 2, 1, 3) - 1;
        quickMode.AccessibleName = "Queued document mode";
        var quickOptions = OutputFolder.LoadQuick();
        quickMode.SelectedIndex = quickOptions.DetectDocuments ? 1 : 0;
        skipBlankBacks.Checked = quickOptions.SkipBlankBacks;
        saveFolder = OutputFolder.Load(quickOptions.DetectDocuments) ?? DefaultSaveFolder;
        devices.Format += (_, e) =>
        {
            if (e.ListItem is ScanDevice device) e.Value = $"{device.Name} ({device.Driver})";
        };

        captureActions.Controls.AddRange([Action("Scan & queue", QuickScan, primary: true),
            Action("Scan for review", ScanForReview),
            Action("Open PDF or images", Open)]);
        analyze = Action("Retry OCR on this page", Analyze);
        save = Action("Save documents", Save);
        cancel = Action("Cancel processing", CancelProcessing);
        ocrText = Action("OCR text…", ShowOcr);
        nextReview = Action("Next OCR issue", NextOcrIssue);
        actions.Controls.AddRange([analyze, save, ocrText, nextReview]);
        settings.Controls.AddRange([
            Caption("Scanner"), devices, Action("Find scanners", () => Run("Looking for scanners…", FindScanners)),
            Caption("Feeder"), sides, Caption("Resolution"), resolution, Caption("OCR"), language,
            Caption("Workers"), workers
        ]);
        output.Controls.AddRange([Action("Save folder…", ChooseDestination), destinationLabel]);
        quickSettings.Controls.AddRange([Caption("Scan profile"), quickMode, skipBlankBacks]);
        destinationLabel.Text = saveFolder;
        destinationLabel.AutoEllipsis = true;
        destinationLabel.MaximumSize = new Size(680, 24);
        back = Action("Previous", () => Navigate(-1));
        next = Action("Next", () => Navigate(1));
        rotate = Action("Rotate 90°", Rotate);
        crop = Action("Preview crop", ToggleCrop);
        remove = Action("Remove page", Remove);
        clear = Action("Clear batch", Clear);
        split = Action("Split before page", ChangeBoundary);
        edits.Controls.AddRange([back, pageNumber, next, rotate, crop, remove, clear, autoOrient, straighten, clean, detail]);

        review.Columns.Add("Page", 49);
        review.Columns.Add("Doc", 43);
        review.Columns.Add("Grouping cue", 169);
        review.Columns.Add("OCR", 89);
        var reviewPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        reviewPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reviewPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reviewPane.Controls.Add(groupLabel);
        reviewPane.Controls.Add(review);
        reviewPane.Controls.Add(split);
        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 368));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.Controls.Add(reviewPane, 0, 0);
        previews.Controls.Add(PreviewPane("Original", original), 1, 0);
        previews.Controls.Add(PreviewPane("Corrected", corrected), 2, 0);
        BuildShell(previews);

        straighten.CheckedChanged += async (_, _) => await ChangeCorrection();
        autoOrient.CheckedChanged += async (_, _) => await ChangeCorrection();
        clean.CheckedChanged += async (_, _) => await ChangeCorrection();
        language.SelectedIndexChanged += async (_, _) => await ChangeCorrection();
        quickMode.SelectedIndexChanged += (_, _) =>
        {
            RememberQuickChoices();
            saveFolder = OutputFolder.Load(GetQuickChoices().DetectDocuments) ?? DefaultSaveFolder;
            destinationLabel.Text = saveFolder;
        };
        skipBlankBacks.CheckedChanged += (_, _) => RememberQuickChoices();
        detail.CheckedChanged += (_, _) => SetZoom();
        review.SelectedIndexChanged += async (_, _) =>
        {
            if (!updatingReview && !busy && review.SelectedIndices.Count == 1 && review.SelectedIndices[0] != current)
                await Navigate(review.SelectedIndices[0] - current);
        };
        FormClosing += (_, e) => e.Cancel = busy || (dirty && pages.Count > 0 &&
            !Confirm(reviewing is null ? "Close and discard this unsaved review batch?" :
                "Close this review and discard your edits? The original pages will stay in the queue."));
        FormClosed += (_, _) =>
        {
            queueTimer.Stop();
            queueTimer.Dispose();
            queueLifetime.Cancel();
            DisposePages();
            engine.Dispose();
        };
        Shown += (_, _) => StartQueue();
        if (initialFile is not null)
            Shown += async (_, _) => await Run("Opening scan…", async () =>
            {
                ShowScreen(Screen.Review);
                await AddPages(engine.Import(initialFile));
                await PrepareBatch();
            });
        UpdateButtons();
    }

    private async Task Scan()
    {
        var device = await SelectScanner();
        var duplex = sides.SelectedIndex == 0;
        await AddPages(engine.Scan(device, duplex ? PaperSource.Duplex : PaperSource.Feeder, ScanDpi,
            operation!.Token), duplex);
        await PrepareBatch();
    }

    private Task ScanForReview() => Run("Scanning…", async () =>
    {
        if (reviewing is not null || reprocessing is not null)
        {
            if (!LeaveCurrentReview()) return;
            DisposePages();
            UpdatePlan();
        }
        ShowScreen(Screen.Review);
        await Scan();
    });

    private QuickFilingOptions GetQuickChoices() => new(quickMode.SelectedIndex == 1, skipBlankBacks.Checked);

    private void RememberQuickChoices()
    {
        try
        {
            OutputFolder.SaveQuick(GetQuickChoices());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = $"Settings could not be remembered. {ex.Message}";
        }
    }

    private int ScanDpi => resolution.SelectedIndex switch { 1 => 400, 2 => 600, _ => 300 };

    private async Task<ScanDevice> SelectScanner()
    {
        if (devices.SelectedItem is null) await FindScanners();
        return devices.SelectedItem as ScanDevice ??
            throw new InvalidOperationException("Choose a scanner. If the list is empty, check the scanner connection and try Find scanners.");
    }

    private async Task FindScanners()
    {
        var found = await engine.Devices();
        var selected = devices.SelectedItem as ScanDevice;
        devices.DisplayMember = nameof(ScanDevice.Name);
        devices.Items.Clear();
        devices.Items.AddRange(found.Cast<object>().ToArray());
        var preferred = selected is null ? -1 : found.FindIndex(item => item.ID == selected.ID && item.Driver == selected.Driver);
        if (preferred < 0)
            preferred = found.FindIndex(DocumentEngine.IsCanonWia);
        if (preferred < 0)
            preferred = found.FindIndex(item => item.Name.Contains("DR-C225", StringComparison.OrdinalIgnoreCase));
        devices.SelectedIndex = preferred < 0 && found.Count == 1 ? 0 : preferred;
        status.Text = found.Count == 0
            ? "No scanner found. Check scanner power and USB, then try again."
            : $"Found {found.Count} scanner connection(s).";
    }

    private async Task Open()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "PDF and image files|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp",
            Multiselect = true,
            Title = "Open existing scans"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        await Run("Opening scans…", async () =>
        {
            if (reviewing is not null || reprocessing is not null)
            {
                if (!LeaveCurrentReview()) return;
                DisposePages();
                UpdatePlan();
            }
            ShowScreen(Screen.Review);
            foreach (var path in dialog.FileNames)
                await AddPages(engine.Import(path));
            await PrepareBatch();
        });
    }

    private async Task AddPages(IAsyncEnumerable<ProcessedImage> incoming, bool duplex = false)
    {
        var start = pages.Count;
        var complete = false;
        try
        {
            await foreach (var image in incoming)
            {
                if (operation!.IsCancellationRequested)
                {
                    image.Dispose();
                    operation.Token.ThrowIfCancellationRequested();
                }
                var side = !duplex ? SheetSide.Unpaired :
                    (pages.Count - start) % 2 == 0 ? SheetSide.Front : SheetSide.Back;
                if (side != SheetSide.Back) nextSheet++;
                pages.Add(new Page(image, side, nextSheet));
                dirty = true;
                status.Text = $"Received {pages.Count - start} page(s)…";
            }
            complete = true;
        }
        finally
        {
            if (pages.Count > start)
            {
                current = start;
                UpdatePlan();
                // The caller will show the completed preview after OCR. Keep interrupted captures visible immediately.
                if (!complete) await RefreshPreview();
            }
        }
        status.Text = pages.Count == start ? "No pages received." : $"{pages.Count} page(s) ready.";
    }

    private Task Analyze() => Run("Retrying OCR on this page…", async () =>
    {
        var page = pages[current];
        page.Options = null;
        await PrepareBatch();
    });

    private ProcessingOptions GetProcessingOptions() => new(straighten.Checked, clean.Checked,
        language.SelectedIndex switch { 1 => "eng", 2 => "deu+eng", _ => "deu" }, autoOrient.Checked);

    private async Task PrepareBatch(bool refreshPreview = true)
    {
        var options = GetProcessingOptions();
        var pending = pages.Where(page => page.Options != options || page.Prepared is null).ToArray();
        var completed = 0;
        var concurrency = workers.SelectedIndex + 1;
        var progress = new Progress<int>(count =>
        {
            status.Text = $"Cleaned and recognized {count} of {pending.Length} pages · {concurrency} workers";
        });
        try
        {
            if (pending.Length > 0)
            {
                status.Text = $"Cleaning and recognizing {pending.Length} pages · {concurrency} workers";
                if (original.Image is null)
                {
                    var page = pages[current];
                    SetImage(original, await Task.Run(() => engine.Preview(page.Image), operation!.Token));
                }
                await Parallel.ForEachAsync(pending, new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = operation!.Token
                }, async (page, token) =>
                {
                    var prepared = await engine.Prepare(page.Image, page.Rotation, options, token, crop: page.Crop);
                    page.Prepared?.Dispose();
                    page.Prepared = prepared;
                    page.Options = options;
                    ((IProgress<int>)progress).Report(Interlocked.Increment(ref completed));
                });
            }
        }
        finally
        {
            UpdatePlan();
            if (refreshPreview) await RefreshPreview();
        }
        var flagged = pages.Count(page => page.Prepared?.NeedsReview == true);
        status.Text = pages.Count == 0 ? "No pages received." : flagged == 0
            ? "Review the proposed groups before saving."
            : $"OCR flagged {flagged} page(s). Use Next OCR issue to check them.";
    }

    private Task ChooseDestination() => Run("Choosing save folder…", async () =>
    {
        await PickDestination();
        status.Text = "Choose a batch to scan or save.";
    });

    private async Task<bool> PickDestination()
    {
        var initial = await Task.Run(() => Directory.Exists(saveFolder)
            ? saveFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to save document PDFs",
            UseDescriptionForTitle = true, InitialDirectory = initial, ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;
        OutputFolder.Save(dialog.SelectedPath, GetQuickChoices().DetectDocuments);
        saveFolder = dialog.SelectedPath;
        destinationLabel.Text = saveFolder;
        return true;
    }

    private Task Save() => Run("Checking the batch and save folder…", async () =>
    {
        var options = GetProcessingOptions();
        if (pages.Any(page => page.Prepared is null || page.Options != options))
        {
            await PrepareBatch();
            status.Text = "Review the updated document groups, then select Save documents again.";
            return;
        }
        var documents = pages.Select((page, index) => (page.Prepared!, groups[index].Number))
            .GroupBy(entry => entry.Number).Select(group => group.Select(entry => entry.Item1).ToArray()).ToArray();
        if (reviewing is { } queued)
        {
            await filingQueue.Approve(queued.Batch, queued.Document, documents, operation!.Token);
            DisposePages();
            UpdatePlan();
            ShowScreen(Screen.History);
            RefreshQueue();
            PumpQueue();
            status.Text = "Approved and queued for delivery.";
            return;
        }
        if (reprocessing is not null)
        {
            using var folder = new FolderBrowserDialog
            {
                Description = "Choose where to save the reviewed copy",
                UseDescriptionForTitle = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (folder.ShowDialog(this) == DialogResult.OK)
                await FileDocuments(documents, folder.SelectedPath);
            else
                status.Text = "Save canceled.";
            return;
        }
        if (!await Task.Run(() => Directory.Exists(saveFolder)) && !await PickDestination())
        {
            status.Text = "Choose an available save folder.";
            return;
        }
        var directory = saveFolder;
        var flagged = pages.Count(page => page.Prepared?.NeedsReview == true);
        var reviewNotice = flagged == 0 ? "" : $"{Environment.NewLine}{Environment.NewLine}" +
            $"{flagged} page(s) have weak or missing OCR.";
        if (!Confirm($"Save {documents.Length} searchable PDF(s) to{Environment.NewLine}{directory}?{reviewNotice}"))
        {
            status.Text = "Save canceled.";
            return;
        }
        await FileDocuments(documents, directory);
    });

    private async Task FileDocuments(PreparedPage[][] documents, string directory)
    {
        status.Text = $"Writing {documents.Length} document(s)…";
        await Task.Run(() => OutputFolder.Export(directory, documents, operation!.Token));
        dirty = false;
        status.Text = $"Saved {documents.Length} searchable PDF(s) · {documents.Sum(doc => doc.Length)} pages.";
    }

    private Task Navigate(int offset) => Run("Rendering preview…", async () =>
    {
        current += offset;
        SelectCurrent();
        await RefreshPreview();
        status.Text = $"{pages.Count} page(s) ready.";
    });

    private Task Rotate() => Run("Rotating preview…", async () =>
    {
        var page = pages[current];
        page.Rotation = ((page.Rotation ?? page.Prepared?.Orientation ?? 0) + 90) % 360;
        page.Options = null;
        dirty = true;
        await PrepareBatch();
    });

    private Task ToggleCrop() => Run("Updating the crop preview…", async () =>
    {
        var page = pages[current];
        page.Rotation ??= page.Prepared?.Orientation;
        page.Crop = !page.Crop;
        page.Options = null;
        dirty = true;
        await PrepareBatch();
        status.Text = page.Crop ? "Crop preview applied to this page. Save to use it, or select Keep full page." :
            "Full page restored.";
    });

    private Task Remove() => Run("Updating batch…", async () =>
    {
        if (pages[current].BreakBefore is true && current + 1 < pages.Count)
            pages[current + 1].BreakBefore ??= true;
        pages[current].Dispose();
        pages.RemoveAt(current);
        current = Math.Clamp(current, 0, Math.Max(0, pages.Count - 1));
        dirty = true;
        UpdatePlan();
        await RefreshPreview();
        status.Text = $"{pages.Count} page(s) ready.";
    });

    private Task Clear()
    {
        if (!Confirm("Clear all pages from this batch?"))
            return Task.CompletedTask;
        DisposePages();
        UpdatePlan();
        status.Text = "Batch cleared.";
        UpdateButtons();
        return Task.CompletedTask;
    }

    private Task ChangeCorrection()
    {
        if (pages.Count == 0)
            return Task.CompletedTask;
        dirty = true;
        return Run("Updating analysis…", () => PrepareBatch());
    }

    private Task ChangeBoundary()
    {
        if (current > 0) SetBoundary(!groups[current].Starts);
        return Task.CompletedTask;
    }

    private void SetBoundary(bool splitHere)
    {
        pages[current].BreakBefore = splitHere;
        dirty = true;
        UpdatePlan();
        UpdateButtons();
        status.Text = splitHere ? "This page starts a new document." : "This page stays with the preceding document.";
    }

    private void UpdatePlan()
    {
        var options = GetProcessingOptions();
        groups = DocumentGroups.Plan(pages.Select(page => new GroupInput(
            page.Options == options ? page.Prepared?.Clues ?? PendingClues : PendingClues,
            page.Side, page.Sheet, page.BreakBefore)).ToArray(), automatic: !oneDocumentBatch);
        review.BeginUpdate();
        updatingReview = true;
        try
        {
            review.Items.Clear();
            for (var i = 0; i < groups.Length; i++)
            {
                var grouping = groups[i];
                var reason = grouping.Reason;
                if (pages[i].Options != options && reason.Length == 0)
                    reason = "Awaiting analysis";
                var prepared = pages[i].Options == options ? pages[i].Prepared : null;
                var reviewText = prepared?.NeedsReview == true ? "Review" :
                    prepared is { Orientation: not 0 } && pages[i].Rotation is null
                        ? $"Auto {prepared.Orientation}°" : "";
                var row = new ListViewItem([(i + 1).ToString(), grouping.Number.ToString(), reason, reviewText]);
                if (grouping.Starts)
                    row.BackColor = Color.FromArgb(223, 236, 248);
                if (prepared?.NeedsReview == true)
                {
                    row.UseItemStyleForSubItems = false;
                    row.SubItems[3].ForeColor = Color.FromArgb(144, 63, 13);
                    row.SubItems[3].BackColor = Color.FromArgb(255, 234, 199);
                }
                review.Items.Add(row);
            }
            SelectCurrent();
            groupLabel.Text = groups.Length == 0 ? "No documents" :
                oneDocumentBatch ? $"{groups[^1].Number} document(s)" : $"{groups[^1].Number} suggested document(s)";
        }
        finally
        {
            updatingReview = false;
            review.EndUpdate();
        }
    }

    private static readonly PageClues PendingClues = new(null, null, null, false, false);

    private void SelectCurrent()
    {
        if (current < review.Items.Count)
        {
            review.Items[current].Selected = true;
            review.Items[current].EnsureVisible();
        }
    }

    private async Task RefreshPreview()
    {
        SetImage(original, null);
        SetImage(corrected, null);
        if (pages.Count == 0)
            return;
        var page = pages[current];
        var options = GetProcessingOptions();
        var (before, after) = await Task.Run(() =>
        {
            var before = engine.Preview(page.Image);
            try
            {
                if (page.Options == options && page.Prepared is not null)
                {
                    using var prepared = Image.FromFile(page.Prepared.Image);
                    return (before, new Bitmap(prepared));
                }
                return (before, engine.Correct(page.Image, page.Rotation ?? 0, options.Straighten, options.Clean, out _));
            }
            catch
            {
                before.Dispose();
                throw;
            }
        });
        SetImage(original, before);
        SetImage(corrected, after);
    }

    private async Task Run(string message, Func<Task> action)
    {
        if (busy)
            return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        busy = true;
        status.Text = message;
        UseWaitCursor = true;
        UpdateButtons();
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status.Text = $"Stopped. {pages.Count} page(s) kept in this batch.";
        }
        catch (Exception ex)
        {
            status.Text = $"{pages.Count} page(s) in the current batch.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            operation = null;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    private Task CancelProcessing()
    {
        operation?.Cancel();
        status.Text = "Stopping after active work…";
        cancel.Enabled = false;
        return Task.CompletedTask;
    }

    private void UpdateButtons()
    {
        actions.Enabled = settings.Enabled = edits.Enabled = output.Enabled = review.Enabled = !busy;
        captureActions.Enabled = quickSettings.Enabled = !busy;
        cancel.Visible = busy;
        cancel.Enabled = busy && operation?.IsCancellationRequested == false;
        save.Enabled = analyze.Enabled = rotate.Enabled = remove.Enabled = clear.Enabled = pages.Count > 0;
        save.Text = reviewing is not null ? "Approve & file" : reprocessing is not null ? "Save copy…" : "Save documents";
        var activePage = pages.Count > 0 ? pages[current] : null;
        crop.Enabled = !busy && activePage is not null && (activePage.Crop || activePage.Prepared?.CropSuggested == true);
        crop.Text = activePage?.Crop == true ? "Keep full page" : "Preview crop";
        var options = GetProcessingOptions();
        ocrText.Enabled = pages.Count > 0 && pages[current].Prepared is not null && pages[current].Options == options;
        nextReview.Enabled = pages.Any(page => page.Options == options && page.Prepared?.NeedsReview == true);
        split.Enabled = !busy && current > 0 && groups.Length > current;
        split.Text = split.Enabled && groups[current].Starts ? "Join previous document" : "Split before page";
        back.Enabled = current > 0;
        next.Enabled = current + 1 < pages.Count;
        pageNumber.Text = pages.Count == 0 ? "No pages" : $"{current + 1} / {pages.Count}";
        if (reviewing is { } active)
        {
            var batch = filingQueue.Get(active.Batch);
            var doc = batch.Documents.Single(doc => doc.Number == active.Document);
            reviewContext.Text = $"Batch {batch.Created.LocalDateTime:dd MMM HH:mm} · Document {doc.Number:D2} · Original pages {doc.FirstPage}–{doc.LastPage}";
            reviewContext.Visible = true;
            if (neighbors is not null)
            {
                var compare = current > 0 && doc.DecisionPage == activePage?.CaptureNumber && doc.DecisionPage is not null &&
                    doc.Reason?.StartsWith("Small item", StringComparison.Ordinal) != true;
                neighbors.Text = compare ? "Compare boundary…" : "Neighbor pages…";
                neighbors.Enabled = compare || doc.FirstPage > 1 || doc.LastPage < batch.Pages;
            }
        }
        else if (reprocessing is { } recovered)
        {
            reviewContext.Text = $"Queue copy · {filingQueue.Get(recovered).Created.LocalDateTime:dd MMM HH:mm}";
            reviewContext.Visible = true;
            if (neighbors is not null) neighbors.Enabled = false;
        }
        else
        {
            reviewContext.Text = "";
            reviewContext.Visible = false;
            if (neighbors is not null) neighbors.Enabled = false;
        }
        UpdateQueueActions();
        if (restoreSides is not null)
            restoreSides.Visible = AvailableOmissions().Length > 0;
    }

    private Task NextOcrIssue()
    {
        var options = GetProcessingOptions();
        for (var offset = 1; offset <= pages.Count; offset++)
        {
            var index = (current + offset) % pages.Count;
            if (pages[index].Options == options && pages[index].Prepared?.NeedsReview == true)
                return Navigate(index - current);
        }
        return Task.CompletedTask;
    }

    private Task ShowOcr()
    {
        var prepared = pages[current].Prepared!;
        using var dialog = new Form
        {
            Text = $"OCR text · page {current + 1}",
            ClientSize = new Size(625, 440),
            MinimumSize = new Size(440, 270),
            StartPosition = FormStartPosition.CenterParent,
            Font = Font,
            ShowInTaskbar = false
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = string.IsNullOrWhiteSpace(prepared.Text) ? "No text recognized." :
                prepared.Text.ReplaceLineEndings(Environment.NewLine),
            Dock = DockStyle.Fill, AccessibleName = "Recognized text"
        };
        var estimate = prepared.Quality.Characters == 0
            ? "No text recognized"
            : $"OCR confidence estimate {prepared.Quality.Confidence:F0}%";
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        footer.Controls.AddRange([close, Caption(estimate)]);
        dialog.Controls.Add(text);
        dialog.Controls.Add(footer);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
        return Task.CompletedTask;
    }

    private void DisposePages()
    {
        foreach (var page in pages)
            page.Dispose();
        pages.Clear();
        automaticReview = null;
        oneDocumentBatch = false;
        reviewing = null;
        reprocessing = null;
        current = 0;
        dirty = false;
        SetImage(original, null);
        SetImage(corrected, null);
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void SetZoom()
    {
        foreach (var picture in new[] { original, corrected })
        {
            picture.Dock = detail.Checked ? DockStyle.None : DockStyle.Fill;
            picture.SizeMode = detail.Checked ? PictureBoxSizeMode.AutoSize : PictureBoxSizeMode.Zoom;
        }
    }

    private static void SetImage(PictureBox picture, Bitmap? bitmap)
    {
        var previous = picture.Image;
        picture.Image = bitmap;
        previous?.Dispose();
    }

    private static TableLayoutPanel PreviewPane(string title, PictureBox image)
    {
        var pane = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.Controls.Add(Caption(title), 0, 0);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(228, 233, 240) };
        image.Dock = DockStyle.Fill;
        image.AccessibleName = title + " page preview";
        viewport.Controls.Add(image);
        pane.Controls.Add(viewport, 0, 1);
        return pane;
    }

    private static Button Action(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Text = title, UseMnemonic = false, AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(202, 214, 228);
        if (primary)
        {
            button.FlatAppearance.BorderColor = Accent;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.EnabledChanged += (_, _) =>
            {
                button.BackColor = button.Enabled ? Accent : Color.FromArgb(222, 231, 241);
                button.FlatAppearance.BorderColor = button.Enabled ? Accent : Color.FromArgb(202, 214, 228);
            };
        }
        button.Click += async (_, _) => await action();
        return button;
    }

    private static ComboBox Options(int width, params string[] options)
    {
        var box = new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 7, 6, 3) };
        box.Items.AddRange(options);
        box.SelectedIndex = 0;
        return box;
    }

    private static FlowLayoutPanel Row() => new()
    {
        Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 2, 0, 4)
    };

    private static Label Caption(string title) => new()
    {
        Text = title, UseMnemonic = false, AutoSize = true, Margin = new Padding(5, 9, 5, 4),
        ForeColor = Color.FromArgb(35, 50, 66)
    };

    private static CheckBox Check(string title, bool enabled) => new()
    {
        Text = title, AutoSize = true, Checked = enabled, Margin = new Padding(8, 8, 3, 3)
    };
}
