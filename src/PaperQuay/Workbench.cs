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
    private readonly ComboBox paperSize = Options(125, ScanPaper.Presets.Select(paper => paper.Name).ToArray());
    private readonly ComboBox colorMode = Options(132, "Color", "Grayscale", "Black & white");
    private readonly ComboBox language = Options(145, "Deutsch", "English", "Deutsch + English");
    private readonly ComboBox reviewOcrLanguage = Options(145, "Deutsch", "English", "Deutsch + English");
    private readonly ComboBox workers = Options(58, "1", "2", "3", "4");
    private readonly ComboBox quickMode = Options(210, "One PDF per load", "Automatic split", "Fixed sheet count");
    private readonly NumericUpDown sheetLimit = new() { Minimum = 1, Maximum = 999, Value = 1, Width = 76,
        AccessibleName = "Sheets per PDF" };
    private readonly CheckBox removeBlankPages = Check("Remove empty pages", true);
    private readonly CheckBox skipBlankBacks = Check("Remove duplex show-through", true);
    private readonly CheckBox scanStraighten = Check("Straighten", true);
    private readonly CheckBox scanAutoOrient = Check("Auto-rotate", true);
    private readonly CheckBox scanClean = Check("Clean background", true);
    private readonly CheckBox twainSettings = Check("Use TWAIN scanner settings", false);
    private readonly CheckBox straighten = Check("Straighten", true);
    private readonly CheckBox autoOrient = Check("Auto-rotate", true);
    private readonly CheckBox clean = Check("Clean background", true);
    private readonly CheckBox detail = Check("100% zoom", false);
    private readonly CheckBox showSkipped = Check("Show skipped pages", false);
    private readonly ComboBox previewMode = Options(150, "Corrected", "Original", "Compare", "Page boundary", "OCR highlights");
    private const int BoundaryView = 3;
    private const int OcrHighlightsView = 4;
    private readonly PictureBox original = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly PictureBox corrected = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label pageNumber = Caption("No pages");
    private readonly Label status = Caption("Ready");
    private readonly FlowLayoutPanel actions = Row();
    private readonly TableLayoutPanel settings = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly FlowLayoutPanel edits = Row();
    private readonly TableLayoutPanel output = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly FlowLayoutPanel reviewTools = Row();
    private readonly Label destinationLabel = Caption("");
    private readonly Label groupLabel = Caption("No documents");
    private readonly ListView review = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill,
        HideSelection = false, ShowItemToolTips = true, AccessibleName = "Pages and suggested documents"
    };
    private readonly Button save;
    private readonly Button back;
    private readonly Button next;
    private readonly Button rotate;
    private readonly Button crop;
    private readonly Button remove;
    private readonly Button clear;
    private readonly Button split;
    private readonly Button join;
    private readonly Button keepEmpty;
    private readonly Button moveUp;
    private readonly Button moveDown;
    private readonly Button analyze;
    private readonly Button cancel;
    private readonly Button ocrText;
    private readonly Button nextReview;
    private int current;
    private bool busy;
    private bool dirty;
    private bool updatingReview;
    private bool oneDocumentBatch;
    private int reviewSheetsPerDocument;
    private QuickFilingOptions reviewFiling = new();
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
        public bool KeepBlank { get; set; }
        public bool Exclude { get; set; }
        public ReverseEvidence? Evidence { get; set; }
        public PreparedPage? Prepared { get; set; }
        public ProcessingOptions? Options { get; set; }
        public void Dispose()
        {
            Image.Dispose();
            Prepared?.Dispose();
        }
    }

    public Workbench(FilingQueue queue, string? initialFile, string? destinationOverride = null)
    {
        filingQueue = queue;
        Text = $"PaperQuay {typeof(Workbench).Assembly.GetName().Version!.ToString(3)}";
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        sides.AccessibleName = "Feeder sides";
        resolution.AccessibleName = "Scan resolution";
        paperSize.AccessibleName = "Paper size";
        colorMode.AccessibleName = "Color mode";
        previewMode.AccessibleName = "Preview mode";
        language.AccessibleName = "OCR language";
        reviewOcrLanguage.AccessibleName = "Review OCR language";
        workers.AccessibleName = "Parallel page workers";
        workers.SelectedIndex = Math.Clamp(Environment.ProcessorCount / 2, 1, 3) - 1;
        quickMode.AccessibleName = "Queued document mode";
        var quickOptions = OutputFolder.LoadQuick();
        SelectGrouping(quickOptions);
        removeBlankPages.Checked = quickOptions.RemovesEmptyPages;
        skipBlankBacks.Checked = quickOptions.SkipBlankBacks;
        saveFolder = destinationOverride ?? OutputFolder.Load(quickOptions.DetectDocuments) ?? DefaultSaveFolder;
        devices.Format += (_, e) =>
        {
            if (e.ListItem is ScanDevice device) e.Value = $"{device.Name} ({device.Driver})";
        };

        captureActions.Controls.AddRange([Action("Scan & queue", QuickScan, primary: true),
            Action("Scan for review", ScanForReview),
            Action("Open PDF or images", Open)]);
        analyze = Action("Retry OCR on this page", Analyze);
        save = Action("Save documents", Save, primary: true);
        cancel = Action("Cancel processing", CancelProcessing);
        ocrText = Action("OCR text…", ShowOcr);
        nextReview = Action("Next OCR issue", NextOcrIssue);
        destinationLabel.Text = saveFolder;
        back = Action("Previous", () => Navigate(FindReviewPage(current, -1)));
        next = Action("Next", () => Navigate(FindReviewPage(current, 1)));
        rotate = Action("Rotate 90°", Rotate);
        crop = Action("Preview crop", ToggleCrop);
        remove = Action("Remove page", Remove);
        clear = Action("Clear batch", Clear);
        split = Action("Split here", () => SetBoundary(true));
        join = Action("Keep together", () => SetBoundary(false));
        keepEmpty = Action("Keep empty page", ToggleEmptyPage);
        moveUp = Action("Move up", () => MovePage(-1));
        moveDown = Action("Move down", () => MovePage(1));
        BuildShell();

        straighten.CheckedChanged += async (_, _) => await ChangeCorrection();
        autoOrient.CheckedChanged += async (_, _) => await ChangeCorrection();
        clean.CheckedChanged += async (_, _) => await ChangeCorrection();
        reviewOcrLanguage.SelectedIndexChanged += async (_, _) => await ChangeCorrection();
        quickMode.SelectedIndexChanged += (_, _) =>
        {
            if (applyingProfile || SelectedProfile is not null) return;
            RememberQuickChoices();
            saveFolder = OutputFolder.Load(GetQuickChoices().DetectDocuments) ?? DefaultSaveFolder;
            destinationLabel.Text = saveFolder;
        };
        sheetLimit.ValueChanged += (_, _) => RememberQuickChoices();
        skipBlankBacks.CheckedChanged += (_, _) => RememberQuickChoices();
        removeBlankPages.CheckedChanged += (_, _) =>
        {
            RememberQuickChoices();
        };
        detail.CheckedChanged += (_, _) => SetZoom();
        showSkipped.CheckedChanged += async (_, _) =>
        {
            if (pages.Count > 0)
                await Run("Updating pages…", async () =>
                {
                    UpdatePlan();
                    await RefreshPreview();
                });
        };
        review.SelectedIndexChanged += async (_, _) =>
        {
            if (!updatingReview && !busy && review.SelectedItems.Count == 1 && review.SelectedItems[0].Tag is int page && page != current)
                await Navigate(page);
        };
        FormClosing += (_, e) => e.Cancel = busy || clearingHistory || (dirty && pages.Count > 0 &&
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
            Shown += async (_, _) => await Run("Opening scan…", () => CaptureForReview(engine.Import(initialFile), duplex: false));
        UpdateButtons();
    }

    private IAsyncEnumerable<ProcessedImage> CapturePages(ScanDevice device, bool duplex, int dpi, ScanPaper paper,
        BitDepth bitDepth, CancellationToken token)
    {
        var parent = twainSettings.Checked && device.Driver == Driver.Twain ? Handle : IntPtr.Zero;
        if (parent != IntPtr.Zero)
            status.Text = $"In TWAIN, select {(duplex ? "Duplex" : "Simplex")} and turn off the driver's blank-page skipping.";
        return DocumentEngine.CaptureWithFeederRecovery(
            () => engine.Scan(device, duplex ? PaperSource.Duplex : PaperSource.Feeder, dpi, paper, bitDepth, token, parent),
            duplex, ConfirmFeederReload, token);
    }

    private bool ConfirmFeederReload(string problem, int sheets)
    {
        status.Text = problem;
        return MessageBox.Show(this, $"{problem}\n\n{ sheets} complete sheet(s) captured in this batch. " +
            "Clear the feeder and reload the affected sheets and the rest of the stack. Select Retry when the feeder is ready.",
            "Resume scanning", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Retry;
    }

    private Task ScanForReview() => Run("Scanning…", async () =>
    {
        var device = await SelectScanner();
        var duplex = sides.SelectedIndex == 0;
        await CaptureForReview(CapturePages(device, duplex, ScanDpi, SelectedPaper, SelectedBitDepth, operation!.Token), duplex);
    });

    private QuickFilingOptions GetQuickChoices() => new(quickMode.SelectedIndex == 1, skipBlankBacks.Checked,
        removeBlankPages.Checked, quickMode.SelectedIndex == 2 ? (int)sheetLimit.Value : 0);

    private void SelectGrouping(QuickFilingOptions options)
    {
        if (options.SheetsPerDocument > 0)
            sheetLimit.Value = Math.Clamp(options.SheetsPerDocument, (int)sheetLimit.Minimum, (int)sheetLimit.Maximum);
        quickMode.SelectedIndex = options.SheetsPerDocument > 0 ? 2 : options.DetectByContent ? 1 : 0;
    }

    private void SetReviewGrouping(QuickFilingOptions options)
    {
        reviewFiling = options;
        oneDocumentBatch = !options.DetectByContent;
        reviewSheetsPerDocument = options.SheetsPerDocument;
    }

    private void StartFreshReview()
    {
        SetReviewGrouping(GetQuickChoices());
        ApplyReviewProcessing(GetScanProcessingOptions());
    }

    private void ApplyReviewProcessing(ProcessingOptions processing)
    {
        reviewOcrLanguage.SelectedIndex = processing.Language switch { "eng" => 1, "deu+eng" => 2, _ => 0 };
        straighten.Checked = processing.Straighten;
        autoOrient.Checked = processing.AutoOrient;
        clean.Checked = processing.Clean;
    }

    private void RememberQuickChoices()
    {
        if (applyingProfile || SelectedProfile is not null) return;
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
    private ScanPaper SelectedPaper => ScanPaper.Presets[Math.Max(0, paperSize.SelectedIndex)];
    private BitDepth SelectedBitDepth => colorMode.SelectedIndex switch
    {
        1 => BitDepth.Grayscale,
        2 => BitDepth.BlackAndWhite,
        _ => BitDepth.Color
    };

    private async Task<ScanDevice> SelectScanner()
    {
        if (devices.SelectedItem is ScanDevice selected) return selected;
        var token = operation!.Token;
        var discovered = await FindScanners();
        if (discovered is null)
        {
            status.Text = "Waiting for Windows to find the scanner…";
            await Task.Delay(350, token);
            discovered = await FindScanners();
        }
        return discovered ??
            throw new InvalidOperationException("Choose a scanner. If the list is empty, check the scanner connection and try Find scanners.");
    }

    private async Task<ScanDevice?> FindScanners()
    {
        var found = new List<ScanDevice>();
        var selected = devices.SelectedItem as ScanDevice;
        var timedOut = false;
        await foreach (var result in engine.DiscoverDevices(operation!.Token))
        {
            if (result is null) timedOut = true;
            else
            {
                found.AddRange(result);
                ShowScanners(found, selected);
                status.Text = $"Found {found.Count} scanner connection(s) so far…";
            }
        }
        var scanner = ShowScanners(found, selected);
        status.Text = timedOut ? $"Found {found.Count} scanner connection(s). A driver did not respond." :
            found.Count == 0 ? "No scanner found. Check scanner power and USB, then try again." :
            scanner is null && SelectedProfile?.ScannerId is not null ? "The profile’s scanner is unavailable. Select a scanner connection." :
                $"Found {found.Count} scanner connection(s).";
        return scanner;
    }

    private ScanDevice? ShowScanners(List<ScanDevice> found, ScanDevice? selected)
    {
        devices.DisplayMember = nameof(ScanDevice.Name);
        devices.Items.Clear();
        devices.Items.AddRange(found.Cast<object>().ToArray());
        var preferred = selected is null ? -1 : found.FindIndex(item => item.ID == selected.ID && item.Driver == selected.Driver);
        var savedScanner = SelectedProfile;
        if (preferred < 0 && savedScanner?.ScannerId is not null)
            preferred = found.FindIndex(item => item.ID == savedScanner.ScannerId && item.Driver == savedScanner.ScannerDriver);
        var missingProfileScanner = preferred < 0 && savedScanner?.ScannerId is not null;
        if (preferred < 0 && !missingProfileScanner)
            preferred = found.FindIndex(DocumentEngine.IsCanonWia);
        if (preferred < 0 && !missingProfileScanner)
            preferred = found.FindIndex(item => item.Name.Contains("DR-C225", StringComparison.OrdinalIgnoreCase));
        if (preferred < 0 && !missingProfileScanner && found.Count == 1) preferred = 0;
        devices.SelectedIndex = preferred;
        return preferred < 0 ? null : found[preferred];
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
        await Run("Opening scans…", () => CaptureForReview(ImportFiles(dialog.FileNames), duplex: false));
    }

    private async IAsyncEnumerable<ProcessedImage> ImportFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            await foreach (var image in engine.Import(path)) yield return image;
    }

    private async Task CaptureForReview(IAsyncEnumerable<ProcessedImage> source, bool duplex)
    {
        QueueBatch batch;
        if (reviewing is { } active && filingQueue.Get(active.Batch).ReviewBeforeFiling)
        {
            if (dirty) RememberReviewEdits();
            batch = filingQueue.AppendReview(active.Batch);
        }
        else
        {
            if (!LeaveCurrentReview()) return;
            batch = filingQueue.Begin(saveFolder, duplex, GetScanProcessingOptions(), GetQuickChoices(),
                workers.SelectedIndex + 1, reviewBeforeFiling: true);
            DisposePages();
            StartFreshReview();
            UpdatePlan();
        }
        ShowScreen(Screen.Review);
        var token = operation!.Token;
        string? error = null;
        try { await AddPages(source, duplex, batch); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { error = "Capture was canceled. Check that all pages are present."; }
        catch (FeederStoppedException ex) { error = ex.Message; }
        catch (Exception ex) { error = $"Capture stopped. Check that all pages are present. {ex.Message}"; }
        var completed = filingQueue.FinishCapture(batch.Id, error, duplex ? batch.Pages : null);
        RefreshQueue(batch.Id);
        if (filingQueue.IsSavingPreparation(batch.Id))
        {
            DisposePages();
            UpdatePlan();
            ShowScreen(Screen.Queue);
            status.Text = "The scanned pages are stored locally. Waiting to save the queue record.";
            return;
        }
        if (completed.Pages > 0)
        {
            reviewing = (batch.Id, 1);
            dirty = false;
            if (!token.IsCancellationRequested) await PrepareBatch();
        }
        if (completed.ScanError is { } issue) status.Text = issue;
    }

    private async Task AddPages(IAsyncEnumerable<ProcessedImage> incoming, bool duplex = false, QueueBatch? draft = null)
    {
        var start = pages.Count;
        var complete = false;
        try
        {
            await foreach (var image in incoming)
            {
                try
                {
                    operation!.Token.ThrowIfCancellationRequested();
                    var number = (draft?.Pages ?? 0) + pages.Count - start + 1;
                    var side = !duplex ? SheetSide.Unpaired : (pages.Count - start) % 2 == 0 ? SheetSide.Front : SheetSide.Back;
                    var sheet = draft is null ? number : side == SheetSide.Back ? number - 1 : number;
                    if (draft is not null)
                        await Task.Run(() =>
                        {
                            using var bitmap = engine.Preview(image);
                            filingQueue.StorePage(draft.Id, number, bitmap, new CaptureLayout(side, sheet));
                        }, operation.Token);
                    pages.Add(new Page(image, side, sheet) { CaptureNumber = number });
                }
                catch { image.Dispose(); throw; }
                dirty = draft is null;
                status.Text = $"Received {pages.Count - start} page(s)…";
            }
            complete = true;
        }
        finally
        {
            if (pages.Count > start)
            {
                current = start;
                if (!complete)
                {
                    UpdatePlan();
                    await RefreshPreview();
                }
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

    private ProcessingOptions GetScanProcessingOptions() => new(scanStraighten.Checked, scanClean.Checked,
        SelectedLanguage(language), scanAutoOrient.Checked);

    private ProcessingOptions GetProcessingOptions() =>
        new(straighten.Checked, clean.Checked, SelectedLanguage(reviewOcrLanguage), autoOrient.Checked);

    private static string SelectedLanguage(ComboBox box) =>
        box.SelectedIndex switch { 1 => "eng", 2 => "deu+eng", _ => "deu" };

    private async Task PrepareBatch()
    {
        var options = GetProcessingOptions();
        var draft = reviewing is { } active && filingQueue.Get(active.Batch) is { ReviewBeforeFiling: true } batch &&
            batch.Processing == options ? batch.Id : (Guid?)null;
        var pending = pages.Where(page => page.Options != options || page.Prepared is null ||
            (page.Prepared.OmitSide && !SkipEmpty(page))).ToArray();
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
                var parallel = new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = operation!.Token
                };
                if (reviewFiling.RemovesEmptyPages || reviewFiling.SkipBlankBacks)
                {
                    var pairs = pages.Where(page => page.Side != SheetSide.Unpaired)
                        .GroupBy(page => page.Sheet)
                        .Select(sheet => (Front: sheet.FirstOrDefault(page => page.Side == SheetSide.Front),
                            Back: sheet.FirstOrDefault(page => page.Side == SheetSide.Back)))
                        .Where(pair => pair.Front is not null && pair.Back is not null &&
                            (pair.Front.Evidence is null || pair.Back.Evidence is null));
                    await Parallel.ForEachAsync(pairs, parallel, (pair, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var front = engine.Preview(pair.Front!.Image);
                        using var back = engine.Preview(pair.Back!.Image);
                        var evidence = ScanImageEvidence.CheckSheet(front, back, reviewFiling);
                        pair.Front.Evidence = evidence[0];
                        pair.Back.Evidence = evidence[1];
                        return ValueTask.CompletedTask;
                    });
                }
                await Parallel.ForEachAsync(pending, parallel, async (page, token) =>
                {
                    var prepared = page.Evidence is { } side && reviewFiling.Omits(side) && SkipEmpty(page)
                        ? PreparedPage.Omitted(side)
                        : await engine.Prepare(page.Image, page.Rotation, options, token, crop: page.Crop,
                            verifiedBlank: page.Evidence is { } evidence ? evidence == ReverseEvidence.Empty : null);
                    if (draft is { } id && page is { Rotation: null, Crop: false, KeepBlank: false, Exclude: false })
                        filingQueue.CachePrepared(id, page.CaptureNumber, prepared with { ReviewSide = page.Evidence == ReverseEvidence.Ambiguous });
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
            await RefreshPreview();
        }
        var flagged = pages.Count(page => page.Prepared?.NeedsReview == true && !SkipEmpty(page));
        status.Text = pages.Count == 0 ? "No pages received." :
            pages.All(SkipEmpty) ? "All pages were skipped. Use Show skipped pages to inspect them." : flagged == 0
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
        if (SelectedProfile is null)
            OutputFolder.Save(dialog.SelectedPath, GetQuickChoices().DetectDocuments);
        saveFolder = dialog.SelectedPath;
        destinationLabel.Text = saveFolder;
        return true;
    }

    private Task Save() => Run("Checking the batch and save folder…", async () =>
    {
        var options = GetProcessingOptions();
        if (pages.Any(page => page.Prepared is null || page.Options != options ||
            (page.Prepared.OmitSide && !SkipEmpty(page))))
        {
            await PrepareBatch();
            status.Text = "Review the updated document groups, then select Save documents again.";
            return;
        }
        var documents = pages.Select((page, index) => (Page: page, groups[index].Number))
            .Where(entry => !SkipEmpty(entry.Page))
            .GroupBy(entry => entry.Number).Select(group => group.Select(entry => entry.Page).ToArray()).ToArray();
        if (documents.Length == 0)
        {
            status.Text = "No PDF created. Use Keep empty page to include a page.";
            return;
        }
        if (reviewing is { } queued)
        {
            if (dirty) RememberReviewEdits();
            await ApproveReview(queued.Batch, queued.Document, documents, AvailableOmissions().Length + pages.Count(SkipEmpty));
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
                await QueueCopy(documents, folder.SelectedPath);
            else
                status.Text = "Save canceled.";
            return;
        }
        await QueueCopy(documents, saveFolder);
    });

    private async Task ApproveReview(Guid id, int number, Page[][] documents, int omitted,
        IReadOnlyDictionary<Page, int>? newSourceNumbers = null)
    {
        var options = GetProcessingOptions();
        var sets = documents.Select(doc => doc.Select(page => page.Prepared!).ToArray()).ToArray();
        var sources = documents.Select(doc => new ReviewDraft(options, doc.Select(page => new PageEdit(
            newSourceNumbers is null ? page.CaptureNumber : newSourceNumbers[page], page.Prepared!.Orientation, page.Crop,
            KeepBlank: true)).ToArray())).ToArray();
        await filingQueue.Approve(id, number, sets, operation!.Token, omitted, sources);
        DisposePages();
        UpdatePlan();
        ShowScreen(Screen.History);
        RefreshQueue(id);
        PumpQueue();
        status.Text = $"Queued {documents.Length} PDF(s) for delivery.";
    }

    private async Task QueueCopy(Page[][] documents, string directory)
    {
        var batch = filingQueue.Begin(directory, false, GetProcessingOptions(), SingleDocument(reviewFiling), workers.SelectedIndex + 1,
            reviewBeforeFiling: true);
        var sourceNumbers = new Dictionary<Page, int>();
        var edits = new List<PageEdit>();
        try
        {
            foreach (var document in documents)
            {
                for (var index = 0; index < document.Length; index++)
                {
                    var page = document[index];
                    var number = sourceNumbers.Count + 1;
                    await Task.Run(() =>
                    {
                        using var image = engine.Preview(page.Image);
                        filingQueue.StorePage(batch.Id, number, image, new CaptureLayout(SheetSide.Unpaired, number));
                    }, operation!.Token);
                    sourceNumbers.Add(page, number);
                    edits.Add(new PageEdit(number, page.Prepared!.Orientation, page.Crop, index == 0, KeepBlank: true));
                }
            }
        }
        finally
        {
            filingQueue.FinishCapture(batch.Id, sourceNumbers.Count == documents.Sum(doc => doc.Length)
                ? null : "Copy preparation was interrupted. Check the stored pages before filing.",
                draft: edits.Count == 0 ? null : new ReviewDraft(GetProcessingOptions(), edits.ToArray()));
            RefreshQueue(batch.Id);
        }
        try
        {
            if (filingQueue.IsSavingPreparation(batch.Id))
                throw new IOException("The copy is saved locally. The queue will retry saving its review record.");
            await ApproveReview(batch.Id, 1, documents, 0, sourceNumbers);
        }
        catch
        {
            DisposePages();
            UpdatePlan();
            ShowScreen(Screen.Queue);
            RefreshQueue(batch.Id);
            throw;
        }
    }

    private void RememberReviewEdits()
    {
        dirty = true;
        if (reviewing is not { } active) return;
        filingQueue.SaveDraft(active.Batch, active.Document, new ReviewDraft(GetProcessingOptions(), pages.Select(page =>
            new PageEdit(page.CaptureNumber, page.Rotation, page.Crop, page.BreakBefore, page.KeepBlank, page.Exclude)).ToArray()));
        dirty = false;
    }

    private Task Navigate(int index) => Run("Rendering preview…", async () =>
    {
        if (index < 0 || index >= pages.Count) return;
        current = index;
        SelectCurrent();
        await RefreshPreview();
        status.Text = $"{pages.Count} page(s) ready.";
    });

    private Task MovePage(int offset) => Run("Moving page…", async () =>
    {
        var destination = FindReviewPage(current, offset);
        if (destination < 0) return;
        (pages[current], pages[destination]) = (pages[destination], pages[current]);
        // Boundary positions belong to the review order, even when a different page occupies that position.
        for (var index = 1; index < pages.Count; index++)
            pages[index].BreakBefore = groups[index].Starts;
        pages[0].BreakBefore = null;
        current = destination;
        UpdatePlan();
        RememberReviewEdits();
        await RefreshPreview();
        status.Text = $"Moved page to position {current + 1}.";
    });

    private Task Rotate() => Run("Rotating preview…", async () =>
    {
        var page = pages[current];
        page.Rotation = ((page.Rotation ?? page.Prepared?.Orientation ?? 0) + 90) % 360;
        page.Options = null;
        RememberReviewEdits();
        await PrepareBatch();
    });

    private Task ToggleCrop() => Run("Updating the crop preview…", async () =>
    {
        var page = pages[current];
        page.Rotation ??= page.Prepared?.Orientation;
        page.Crop = !page.Crop;
        page.Options = null;
        RememberReviewEdits();
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
        UpdatePlan();
        RememberReviewEdits();
        await RefreshPreview();
        status.Text = $"{pages.Count} page(s) ready.";
    });

    private Task Clear() => Run("Clearing review…", () =>
    {
        if (!Confirm("Clear all pages from this batch?"))
            return Task.CompletedTask;
        if (reviewing is { } queued)
        {
            filingQueue.Discard(queued.Batch, queued.Document);
            RefreshQueue();
        }
        DisposePages();
        UpdatePlan();
        status.Text = "Batch cleared.";
        UpdateButtons();
        return Task.CompletedTask;
    });

    private Task ChangeCorrection()
    {
        if (applyingProfile || pages.Count == 0)
            return Task.CompletedTask;
        return Run("Updating analysis…", async () =>
        {
            RememberReviewEdits();
            await PrepareBatch();
        });
    }

    private Task SetBoundary(bool splitHere) => Run("Updating document groups…", () =>
    {
        pages[current].BreakBefore = splitHere;
        UpdatePlan();
        RememberReviewEdits();
        status.Text = splitHere ? "This page starts a new document." : "This page stays with the preceding document.";
        return Task.CompletedTask;
    });

    private bool IsOmittable(Page page) => page.Evidence == ReverseEvidence.ShowThrough
        ? reviewFiling.SkipBlankBacks
        : reviewFiling.RemovesEmptyPages &&
            (page.Evidence == ReverseEvidence.Empty || page.Prepared?.VerifiedBlank == true);

    private static bool NeedsFoldReview(Page page) =>
        page.Prepared?.ReviewSide == true || page.Evidence == ReverseEvidence.Ambiguous;

    private bool SkipEmpty(Page page) => page.Exclude || (!page.KeepBlank && IsOmittable(page));

    private bool ShowsReviewPage(int index) => showSkipped.Checked || !SkipEmpty(pages[index]);

    private int FindReviewPage(int start, int step)
    {
        for (var index = start + step; index >= 0 && index < pages.Count; index += step)
            if (ShowsReviewPage(index)) return index;
        return -1;
    }

    private int PreviousContent() => current == 0 ? -1 : pages.FindLastIndex(current - 1, page => !SkipEmpty(page));

    private void PaintOcrHighlights(object? sender, PaintEventArgs e)
    {
        if (previewMode.SelectedIndex != OcrHighlightsView || current >= pages.Count ||
            corrected.Image is not { } bitmap ||
            pages[current].Prepared?.WeakWords is not { Length: > 0 } words ||
            pages[current].Options != GetProcessingOptions())
            return;
        var scale = detail.Checked ? 1f : Math.Min(corrected.ClientSize.Width / (float)bitmap.Width,
            corrected.ClientSize.Height / (float)bitmap.Height);
        if (scale <= 0) return;
        var offsetX = (corrected.ClientSize.Width - bitmap.Width * scale) / 2;
        var offsetY = (corrected.ClientSize.Height - bitmap.Height * scale) / 2;
        using var fill = new SolidBrush(Color.FromArgb(54, 245, 167, 37));
        using var pen = new Pen(Color.FromArgb(202, 123, 20), 1);
        foreach (var word in words)
        {
            if (word.X < 0 || word.Y < 0 || word.Width <= 0 || word.Height <= 0 ||
                word.X > bitmap.Width - word.Width || word.Y > bitmap.Height - word.Height)
                continue;
            var bounds = new RectangleF(offsetX + word.X * scale, offsetY + word.Y * scale,
                word.Width * scale, word.Height * scale);
            e.Graphics.FillRectangle(fill, bounds);
            e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    private Task ToggleEmptyPage() => Run("Updating page…", async () =>
    {
        var page = pages[current];
        if (NeedsFoldReview(page)) page.Exclude = !page.Exclude;
        else page.KeepBlank = !page.KeepBlank;
        RememberReviewEdits();
        if (!SkipEmpty(page) && page.Prepared?.OmitSide == true)
            await PrepareBatch();
        else
        {
            UpdatePlan();
            await RefreshPreview();
        }
        status.Text = SkipEmpty(page) ? "This page will be skipped." : "This page will be included.";
    });

    private void UpdatePlan()
    {
        UpdateReviewVisibility();
        var options = GetProcessingOptions();
        groups = DocumentGroups.Plan(pages.Select(page => new GroupInput(
            page.Options == options && page.Prepared is { } prepared
                ? prepared.Clues with { Blank = prepared.VerifiedBlank || SkipEmpty(page) } : PendingClues,
            page.Side, page.Sheet, page.BreakBefore, page.Prepared?.TrustsPageNumber == true)).ToArray(), automatic: !oneDocumentBatch,
            sheetsPerDocument: reviewSheetsPerDocument);
        review.BeginUpdate();
        updatingReview = true;
        try
        {
            review.Items.Clear();
            showSkipped.Visible = pages.Any(SkipEmpty);
            if (pages.Count > 0 && !ShowsReviewPage(current))
            {
                var nextPage = FindReviewPage(current, 1);
                var previousPage = FindReviewPage(current, -1);
                if (nextPage >= 0 || previousPage >= 0) current = nextPage >= 0 ? nextPage : previousPage;
            }
            for (var i = 0; i < groups.Length; i++)
            {
                if (!ShowsReviewPage(i)) continue;
                var grouping = groups[i];
                var reason = grouping.Reason;
                if (pages[i].Options != options && reason.Length == 0)
                    reason = "Awaiting analysis";
                var prepared = pages[i].Options == options ? pages[i].Prepared : null;
                var skipped = SkipEmpty(pages[i]);
                var reviewText = skipped ? "Skipped" :
                    NeedsFoldReview(pages[i]) ? "Check fold" :
                    prepared?.VerifiedBlank == true ? "Empty" :
                    prepared?.NeedsReview == true ? "Review" :
                    prepared is { Orientation: not 0 } && pages[i].Rotation is null
                        ? $"Auto {prepared.Orientation}°" : "";
                var row = new ListViewItem([(i + 1).ToString(), skipped ? "—" : grouping.Number.ToString(), reviewText])
                {
                    Tag = i,
                    ToolTipText = skipped ? "Skipped page. Use the Keep action to include it." : reason
                };
                if (grouping.Starts)
                    row.BackColor = AccentTint;
                if (prepared?.NeedsReview == true)
                {
                    row.UseItemStyleForSubItems = false;
                    row.SubItems[2].ForeColor = Color.FromArgb(144, 63, 13);
                    row.SubItems[2].BackColor = Color.FromArgb(255, 234, 199);
                }
                review.Items.Add(row);
            }
            SelectCurrent();
            var documentCount = groups.Where((_, index) => !SkipEmpty(pages[index]))
                .Select(group => group.Number).Distinct().Count();
            groupLabel.Text = documentCount == 0 ? "No documents" :
                oneDocumentBatch ? $"{documentCount} document(s)" : $"{documentCount} suggested document(s)";
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
        var row = review.Items.Cast<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, current));
        if (row is not null)
        {
            row.Selected = true;
            row.EnsureVisible();
        }
    }

    private async Task RefreshPreview()
    {
        SetImage(original, null);
        SetImage(corrected, null);
        if (pages.Count == 0 || !ShowsReviewPage(current))
            return;
        var page = pages[current];
        var options = GetProcessingOptions();
        var boundary = previewMode.SelectedIndex == BoundaryView;
        var previous = boundary ? PreviousContent() : -1;
        var neighbor = previous < 0 ? null : pages[previous];
        Bitmap CorrectedPreview(Page item)
        {
            if (item.Options == options && item.Prepared is { OmitSide: false } prepared)
            {
                using var image = Image.FromFile(prepared.Image);
                return new Bitmap(image);
            }
            return engine.Correct(item.Image, item.Rotation ?? 0, options.Straighten, options.Clean, out _);
        }
        var (before, after) = await Task.Run(() =>
        {
            var before = boundary ? neighbor is null ? null : CorrectedPreview(neighbor) : engine.Preview(page.Image);
            try
            {
                return (before, CorrectedPreview(page));
            }
            catch
            {
                before?.Dispose();
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

    private void UpdateReviewVisibility()
    {
        var hasPages = pages.Count > 0;
        actions.Visible = edits.Visible = reviewBody.Visible = hasPages;
        reviewPrompt.Visible = !hasPages;
        if (!hasPages) reviewTools.Visible = false;
    }

    private void UpdateButtons()
    {
        UpdateReviewVisibility();
        var hasPages = pages.Count > 0;
        reviewPrompt.Enabled = !busy;
        actions.Enabled = settings.Enabled = edits.Enabled = output.Enabled = review.Enabled = !busy;
        captureActions.Enabled = reviewTools.Enabled = !busy;
        if (profileControls is not null) profileControls.Enabled = !busy;
        cancel.Visible = busy;
        cancel.Enabled = busy && operation?.IsCancellationRequested == false;
        clear.Enabled = hasPages;
        save.Enabled = pages.Any(page => !SkipEmpty(page));
        showSkipped.Enabled = !busy;
        save.Text = reviewing is not null ? "Approve & file" : reprocessing is not null ? "Save copy…" : "Save documents";
        var activePage = hasPages && ShowsReviewPage(current) ? pages[current] : null;
        analyze.Enabled = rotate.Enabled = remove.Enabled = activePage is not null;
        crop.Enabled = !busy && activePage is not null && (activePage.Crop || activePage.Prepared?.CropSuggested == true);
        crop.Text = activePage?.Crop == true ? "Keep full page" : "Preview crop";
        var options = GetProcessingOptions();
        ocrText.Enabled = activePage is { Prepared.OmitSide: false } && activePage.Options == options;
        nextReview.Enabled = pages.Any(page => !SkipEmpty(page) && page.Options == options && page.Prepared?.NeedsReview == true);
        split.Visible = join.Visible = activePage is not null && !SkipEmpty(activePage) && PreviousContent() >= 0;
        split.Enabled = join.Enabled = !busy && groups.Length > current;
        var fold = activePage is not null && NeedsFoldReview(activePage);
        keepEmpty.Visible = activePage is not null && (fold || IsOmittable(activePage));
        keepEmpty.Text = fold ? activePage!.Exclude ? "Restore this side" : "Skip this side" :
            activePage?.Evidence == ReverseEvidence.ShowThrough
                ? activePage.KeepBlank ? "Skip reverse" : "Keep reverse"
                : activePage?.KeepBlank == true ? "Skip empty page" : "Keep empty page";
        back.Enabled = FindReviewPage(current, -1) >= 0;
        next.Enabled = FindReviewPage(current, 1) >= 0;
        moveUp.Enabled = !busy && back.Enabled;
        moveDown.Enabled = !busy && next.Enabled;
        pageNumber.Text = review.SelectedItems.Count == 0 ? "No pages" :
            $"{review.SelectedItems[0].Index + 1} / {review.Items.Count}";
        if (reviewing is { } active)
        {
            var batch = filingQueue.Get(active.Batch);
            var doc = batch.Documents.Single(doc => doc.Number == active.Document);
            reviewContext.Text = $"Batch {batch.Created.LocalDateTime:dd MMM HH:mm} · Document {doc.Number:D2} · Original pages {doc.FirstPage}–{doc.LastPage}";
            if (!string.IsNullOrWhiteSpace(doc.Reason))
                reviewContext.Text += Environment.NewLine + doc.Reason;
            reviewContext.Visible = true;
            if (neighbors is not null)
            {
                var available = doc.FirstPage > 1 || doc.LastPage < batch.Pages;
                neighbors.Enabled = !busy && available;
                neighbors.Visible = available;
            }
        }
        else if (reprocessing is { } recovered)
        {
            reviewContext.Text = $"Queue copy · {filingQueue.Get(recovered).Created.LocalDateTime:dd MMM HH:mm}";
            reviewContext.Visible = true;
            if (neighbors is not null) neighbors.Visible = false;
        }
        else
        {
            reviewContext.Text = "";
            reviewContext.Visible = false;
            if (neighbors is not null) neighbors.Visible = false;
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
            if (!SkipEmpty(pages[index]) && pages[index].Options == options && pages[index].Prepared?.NeedsReview == true)
                return Navigate(index);
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
        showSkipped.Checked = false;
        automaticReview = null;
        oneDocumentBatch = false;
        reviewSheetsPerDocument = 0;
        reviewing = null;
        reprocessing = null;
        current = 0;
        dirty = false;
        previewMode.SelectedIndex = 0;
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
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PreviewSurface };
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
            Text = title, UseMnemonic = false, AutoSize = true, Height = 35, Padding = new Padding(9, 0, 9, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink
        };
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.MouseOverBackColor = AccentTint;
        if (primary)
        {
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = RailSelection;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.EnabledChanged += (_, _) =>
            {
                button.BackColor = button.Enabled ? Accent : AccentTint;
                button.FlatAppearance.BorderColor = button.Enabled ? Accent : Line;
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
        ForeColor = Ink
    };

    private static CheckBox Check(string title, bool enabled) => new()
    {
        Text = title, AutoSize = true, Checked = enabled, Margin = new Padding(8, 8, 3, 3)
    };
}
