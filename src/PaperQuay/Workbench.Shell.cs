using NAPS2.Scan;

namespace PaperQuay;

internal sealed partial class Workbench
{
    private enum Screen { Scan, Queue, Decisions, History, Review }
    private static readonly Color Ink = Color.FromArgb(28, 42, 36);
    private static readonly Color Muted = Color.FromArgb(77, 93, 83);
    private static readonly Color Canvas = Color.FromArgb(244, 243, 238);
    private static readonly Color PreviewSurface = Color.FromArgb(225, 229, 222);
    private static readonly Color Line = Color.FromArgb(200, 211, 203);
    private static readonly Color Rail = Color.FromArgb(20, 36, 29);
    private static readonly Color RailSelection = Color.FromArgb(44, 72, 60);
    private static readonly Color Accent = Color.FromArgb(25, 103, 77);
    private static readonly Color AccentTint = Color.FromArgb(229, 240, 233);
    private readonly FlowLayoutPanel captureActions = Row();
    private readonly Panel workspace = new() { Dock = DockStyle.Fill, BackColor = Canvas, Margin = Padding.Empty };
    private readonly Dictionary<Screen, Control> screens = [];
    private readonly Dictionary<Screen, Button> nav = [];
    private readonly Label waitingKpi = MetricValue();
    private readonly Label reviewKpi = MetricValue();
    private readonly Label filedKpi = MetricValue();
    private readonly Label reviewContext = new()
    {
        AutoSize = true, ForeColor = Muted, Margin = new Padding(3, 0, 0, 10)
    };
    private Button? neighbors;
    private Control reviewBody = null!;
    private Control reviewPrompt = null!;
    private Label queueTitle = null!;
    private Screen activeScreen;

    private void BuildShell()
    {
        BackColor = Canvas;
        ClientSize = new Size(1360, 810);
        MinimumSize = new Size(1050, 690);
        KeyPreview = true;
        KeyDown += async (_, flags) =>
        {
            if (activeScreen != Screen.Scan || busy || !flags.Control || flags.KeyCode != Keys.Enter) return;
            flags.SuppressKeyPress = true;
            await QuickScan();
        };

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(15, 24, 15, 0),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Rail
        };
        navigation.Controls.Add(new Label
        {
            Text = "PaperQuay", Font = new Font("Segoe UI Semibold", 17), ForeColor = Color.White,
            AutoSize = true, Margin = new Padding(3, 0, 0, 28)
        });
        foreach (var screen in new[] { Screen.Scan, Screen.Queue, Screen.Decisions, Screen.History, Screen.Review })
        {
            var button = new Button
            {
                Text = screen == Screen.Decisions ? "Needs a decision" : screen.ToString(),
                Width = 158, Height = 42, Margin = new Padding(0, 0, 0, 5),
                UseMnemonic = false, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(11, 0, 0, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = RailSelection;
            button.Click += (_, _) => ShowScreen(screen);
            nav.Add(screen, button);
            navigation.Controls.Add(button);
        }
        shell.Controls.Add(navigation, 0, 0);
        shell.SetRowSpan(navigation, 2);
        shell.Controls.Add(workspace, 1, 0);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Color.White,
            Margin = Padding.Empty, Padding = new Padding(22, 2, 16, 2)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.Height = 31;
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = Muted;
        status.Margin = Padding.Empty;
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        shell.Controls.Add(footer, 1, 1);
        Controls.Add(shell);

        screens.Add(Screen.Scan, BuildScanScreen());
        var queue = PageLayout("History");
        queueTitle = (Label)queue.Controls[0];
        AddRow(queue, BuildQueueView(), fill: true);
        screens.Add(Screen.Queue, queue);
        screens.Add(Screen.Decisions, queue);
        screens.Add(Screen.History, queue);
        screens.Add(Screen.Review, BuildReviewScreen());
        foreach (var screen in screens.Values.Distinct()) workspace.Controls.Add(screen);
        ShowScreen(Screen.Scan);
    }

    private Control BuildScanScreen()
    {
        var scan = PageLayout("Scan");
        AddRow(scan, BuildProfilePicker());
        var setup = SideBySide(55);
        var capture = Card("Scan setup");
        settings.ColumnCount = 2;
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.Margin = Padding.Empty;

        var scanner = SideBySide(100);
        scanner.ColumnStyles[1].SizeType = SizeType.AutoSize;
        scanner.Controls.Add(devices, 0, 0);
        scanner.Controls.Add(Action("Find scanners", () => Run("Looking for scanners…", FindScanners)), 1, 0);
        devices.Dock = DockStyle.Fill;
        devices.Margin = new Padding(0, 7, 6, 0);
        var selector = Field("Scanner connection", scanner);
        settings.Controls.Add(selector, 0, 0);
        settings.SetColumnSpan(selector, 2);
        settings.Controls.Add(Field("Paper size", paperSize), 0, 1);
        settings.Controls.Add(Field("Color mode", colorMode), 1, 1);
        settings.Controls.Add(Field("Sides", sides), 0, 2);
        settings.Controls.Add(Field("Resolution", resolution), 1, 2);
        settings.Controls.Add(Field("OCR language", language), 0, 3);
        settings.Controls.Add(Field("OCR workers", workers), 1, 3);
        var corrections = Row();
        corrections.Controls.AddRange([scanStraighten, scanAutoOrient, scanClean]);
        settings.Controls.Add(corrections, 0, 4);
        settings.SetColumnSpan(corrections, 2);
        settings.Controls.Add(twainSettings, 0, 5);
        settings.SetColumnSpan(twainSettings, 2);
        void UpdateTwainSettings()
        {
            var available = devices.SelectedItem is ScanDevice { Driver: Driver.Twain };
            twainSettings.Visible = available;
            paperSize.Enabled = colorMode.Enabled = resolution.Enabled = !available || !twainSettings.Checked;
        }
        devices.SelectedIndexChanged += (_, _) => UpdateTwainSettings();
        twainSettings.CheckedChanged += (_, _) => UpdateTwainSettings();
        UpdateTwainSettings();
        AddRow(capture, settings);
        setup.Controls.Add(capture, 0, 0);

        var destination = Card("Filing");
        destination.Margin = Padding.Empty;
        output.ColumnCount = 1;
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        output.Margin = Padding.Empty;
        destinationLabel.AutoSize = false;
        destinationLabel.AutoEllipsis = true;
        destinationLabel.Height = 40;
        destinationLabel.Padding = new Padding(10, 0, 8, 0);
        destinationLabel.TextAlign = ContentAlignment.MiddleLeft;
        destinationLabel.BackColor = Canvas;
        AddRow(output, Field("Destination folder", destinationLabel));
        var folderAction = Action("Save folder…", ChooseDestination);
        folderAction.Anchor = AnchorStyles.Left;
        folderAction.Margin = new Padding(0, 0, 0, 17);
        AddRow(output, folderAction);
        var grouping = SideBySide(67);
        grouping.Controls.Add(Field("Batch grouping", quickMode), 0, 0);
        var limit = Field("Sheets per PDF", sheetLimit);
        grouping.Controls.Add(limit, 1, 0);
        limit.Visible = quickMode.SelectedIndex == 2;
        quickMode.SelectedIndexChanged += (_, _) => limit.Visible = quickMode.SelectedIndex == 2;
        AddRow(output, grouping);
        removeBlankPages.Margin = new Padding(1, 1, 0, 0);
        skipBlankBacks.Margin = new Padding(1, 1, 0, 0);
        AddRow(output, removeBlankPages);
        AddRow(output, skipBlankBacks);
        AddRow(destination, output);
        setup.Controls.Add(destination, 1, 0);
        AddRow(scan, setup);

        captureActions.Padding = new Padding(0, 2, 0, 2);
        captureActions.Controls[0].MinimumSize = new Size(153, 40);
        AddRow(scan, captureActions);

        var summary = new TableLayoutPanel
        {
            ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.White,
            Padding = new Padding(17, 10, 12, 9), Margin = new Padding(0, 14, 0, 0)
        };
        foreach (var (title, count) in new[]
                 { ("In progress", waitingKpi), ("Needs a decision", reviewKpi), ("Filed PDFs", filedKpi) })
        {
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var metric = Row();
            metric.Controls.AddRange([count, Caption(title)]);
            summary.Controls.Add(metric);
        }
        AddRow(scan, summary);
        AddRow(scan, new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, fill: true);
        return scan;
    }

    private Control BuildReviewScreen()
    {
        var screen = PageLayout("Review");
        AddRow(screen, reviewContext);
        neighbors = Action("Adjacent documents…", ShowNeighbors);
        restoreSides = Action("Restore omitted side…", RestoreOmittedSide);
        neighbors.Visible = restoreSides.Visible = false;
        var tools = Action("Review tools", () =>
        {
            reviewTools.Visible = !reviewTools.Visible;
            return Task.CompletedTask;
        });
        actions.Controls.AddRange([save, split, join, keepEmpty, ocrText, neighbors, restoreSides, tools]);
        AddRow(screen, actions);
        reviewTools.Controls.AddRange([analyze, nextReview, remove, clear, autoOrient, straighten, clean,
            Caption("OCR language"), reviewOcrLanguage]);
        reviewTools.BackColor = Color.White;
        reviewTools.Padding = new Padding(5, 4, 4, 4);
        reviewTools.Visible = false;
        AddRow(screen, reviewTools);
        edits.Controls.AddRange([back, pageNumber, next, Caption("View"), previewMode, detail, rotate, crop]);
        AddRow(screen, edits);

        review.Columns.Add("Page", 48);
        review.Columns.Add("Doc", 45);
        review.Columns.Add("Status", 77);
        review.BorderStyle = BorderStyle.None;
        var pagesPanel = Card("Pages");
        AddRow(pagesPanel, review, fill: true);
        var pageOrder = Row();
        pageOrder.Controls.AddRange([moveUp, moveDown]);
        AddRow(pagesPanel, pageOrder);
        showSkipped.Visible = false;
        AddRow(pagesPanel, showSkipped);
        AddRow(pagesPanel, groupLabel);
        pagesPanel.AutoSize = false;
        pagesPanel.Margin = new Padding(0, 0, 12, 0);

        var views = SideBySide(50);
        views.AutoSize = false;
        var originalPane = PreviewPane("Original", original);
        var correctedPane = PreviewPane("Corrected", corrected);
        corrected.Paint += PaintOcrHighlights;
        var originalTitle = originalPane.GetControlFromPosition(0, 0)!;
        var correctedTitle = correctedPane.GetControlFromPosition(0, 0)!;
        views.Controls.Add(originalPane, 0, 0);
        views.Controls.Add(correctedPane, 1, 0);
        void SetView()
        {
            var mode = previewMode.SelectedIndex;
            views.SuspendLayout();
            try
            {
                originalPane.Visible = mode is not (0 or OcrHighlightsView);
                correctedPane.Visible = mode != 1;
                originalTitle.Text = mode == BoundaryView ? "Previous page" : "Original";
                correctedTitle.Text = mode switch
                {
                    BoundaryView => "Current page",
                    OcrHighlightsView => "Words with low OCR confidence",
                    _ => "Corrected"
                };
                views.ColumnStyles[0].Width = mode switch { 0 or OcrHighlightsView => 0, 1 => 100, _ => 50 };
                views.ColumnStyles[1].Width = 100 - views.ColumnStyles[0].Width;
            }
            finally { views.ResumeLayout(); }
        }
        var comparingBoundary = false;
        previewMode.SelectedIndexChanged += async (_, _) =>
        {
            SetView();
            var boundary = previewMode.SelectedIndex == BoundaryView;
            var refresh = comparingBoundary != boundary;
            comparingBoundary = boundary;
            if (refresh && pages.Count > 0 && !busy)
                await Run("Opening page previews…", RefreshPreview);
            corrected.Invalidate();
        };
        SetView();

        var body = SideBySide(0);
        body.AutoSize = false;
        body.ColumnStyles[0].SizeType = SizeType.Absolute;
        body.ColumnStyles[0].Width = 221;
        body.ColumnStyles[1].Width = 100;
        body.Controls.Add(pagesPanel, 0, 0);
        body.Controls.Add(views, 1, 0);
        reviewBody = body;
        var prompt = Card("No document open");
        prompt.Dock = DockStyle.Top;
        var open = Action("Open PDF or images", Open, primary: true);
        open.Anchor = AnchorStyles.Left;
        AddRow(prompt, open);
        reviewPrompt = prompt;
        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        content.Controls.Add(body);
        content.Controls.Add(prompt);
        prompt.BringToFront();
        AddRow(screen, content, fill: true);
        screen.SizeChanged += (_, _) => reviewContext.MaximumSize = new Size(Math.Max(100, screen.Width - 60), 0);
        return screen;
    }

    private void ShowScreen(Screen screen)
    {
        activeScreen = screen;
        foreach (var panel in screens.Values.Distinct())
            panel.Visible = ReferenceEquals(panel, screens[screen]);
        foreach (var (key, button) in nav)
        {
            button.BackColor = key == screen ? RailSelection : Rail;
            button.ForeColor = key == screen ? Color.White : Color.FromArgb(211, 226, 218);
        }
        screens[screen].BringToFront();
        if (ShowingQueue)
        {
            queueTitle.Text = screen == Screen.Decisions ? "Needs a decision" : screen.ToString();
            RefreshQueue();
        }
    }

    private static TableLayoutPanel PageLayout(string title)
    {
        var panel = Column();
        panel.AutoSize = false;
        panel.Padding = new Padding(25, 20, 24, 14);
        AddRow(panel, new Label
        {
            Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 26), ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 17), UseMnemonic = false
        });
        return panel;
    }

    private static TableLayoutPanel Card(string title)
    {
        var card = Column();
        card.BackColor = Color.White;
        card.Margin = new Padding(0, 0, 10, 0);
        card.Padding = new Padding(15, 12, 13, 10);
        AddRow(card, new Label
        {
            Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 13), ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 8), UseMnemonic = false
        });
        return card;
    }

    private static TableLayoutPanel Field(string title, Control control)
    {
        var field = Column();
        field.Margin = new Padding(0, 0, 9, 10);
        var label = Caption(title);
        label.Margin = new Padding(0, 0, 0, 4);
        label.ForeColor = Muted;
        AddRow(field, label);
        control.Dock = DockStyle.Fill;
        control.Margin = Padding.Empty;
        AddRow(field, control);
        return field;
    }

    private static TableLayoutPanel Column()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private static TableLayoutPanel SideBySide(float leftWidth)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftWidth));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftWidth));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return panel;
    }

    private static void AddRow(TableLayoutPanel panel, Control control, bool fill = false)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(fill ? SizeType.Percent : SizeType.AutoSize, fill ? 100 : 0));
        panel.Controls.Add(control, 0, row);
    }

    private static Label MetricValue() => new()
    {
        Text = "0", AutoSize = true, Font = new Font("Segoe UI Semibold", 17), ForeColor = Accent,
        Margin = new Padding(2, 2, 4, 0)
    };
}
