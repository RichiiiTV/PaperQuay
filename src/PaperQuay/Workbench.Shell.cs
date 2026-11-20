namespace PaperQuay;

internal sealed partial class Workbench
{
    private enum Screen { Scan, Decisions, History, Review }
    private static readonly Color Ink = Color.FromArgb(25, 41, 60);
    private static readonly Color Muted = Color.FromArgb(86, 106, 126);
    private static readonly Color Canvas = Color.FromArgb(242, 246, 251);
    private static readonly Color Rail = Color.FromArgb(20, 35, 53);
    private static readonly Color Accent = Color.FromArgb(22, 100, 142);
    private readonly FlowLayoutPanel captureActions = Row();
    private readonly FlowLayoutPanel quickSettings = Row();
    private readonly Panel workspace = new() { Dock = DockStyle.Fill, BackColor = Canvas };
    private readonly Dictionary<Screen, Control> screens = [];
    private readonly Dictionary<Screen, Button> nav = [];
    private readonly Label waitingKpi = MetricValue();
    private readonly Label reviewKpi = MetricValue();
    private readonly Label filedKpi = MetricValue();
    private readonly Label reviewContext = new()
    {
        AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 1, 0, 8)
    };
    private Button? neighbors;
    private Label queueTitle = null!;
    private Label queueDescription = null!;
    private Screen activeScreen;

    private void BuildShell(Control previews)
    {
        BackColor = Canvas;
        ClientSize = new Size(1450, 870);
        MinimumSize = new Size(1130, 700);
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
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16, 22, 16, 0),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Rail
        };
        navigation.Controls.Add(new Label
        {
            Text = "PaperQuay", Font = new Font("Segoe UI Semibold", 14), ForeColor = Color.White,
            AutoSize = true, Margin = new Padding(3, 0, 0, 26)
        });
        foreach (var screen in new[] { Screen.Scan, Screen.Decisions, Screen.History, Screen.Review })
        {
            var button = new Button
            {
                Text = screen == Screen.Decisions ? "Needs a decision" : screen.ToString(),
                Width = 156, Height = 43, Margin = new Padding(0, 0, 0, 6),
                UseMnemonic = false, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(11, 0, 0, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowScreen(screen);
            nav.Add(screen, button);
            navigation.Controls.Add(button);
        }
        shell.Controls.Add(navigation, 0, 0);
        shell.SetRowSpan(navigation, 2);
        shell.Controls.Add(workspace, 1, 0);
        var progress = Row();
        progress.BackColor = Color.White;
        progress.Padding = new Padding(18, 6, 8, 6);
        progress.Controls.AddRange([cancel, status]);
        status.AutoEllipsis = true;
        status.MaximumSize = new Size(1010, 44);
        shell.Controls.Add(progress, 1, 1);
        Controls.Add(shell);

        var scan = PageLayout("Scan", "Load the feeder. Queued batches are processed in the background.");
        var capture = Card("Scanner");
        capture.Controls.Add(settings);
        capture.Controls.Add(captureActions);
        capture.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        capture.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scan.Controls.Add(capture);
        scan.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
        var destination = Card("Filing");
        destination.Controls.Add(output);
        destination.Controls.Add(quickSettings);
        destination.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        destination.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scan.Controls.Add(destination);
        scan.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        var summary = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0)
        };
        foreach (var (title, label) in new[]
        {
            ("In progress", waitingKpi), ("Needs a decision", reviewKpi), ("Filed PDFs", filedKpi)
        })
        {
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var block = Card(title);
            block.Margin = new Padding(0, 0, 12, 0);
            block.Controls.Add(label);
            block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(block);
        }
        scan.Controls.Add(summary);
        scan.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        scan.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty });
        scan.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        screens.Add(Screen.Scan, scan);

        var queue = PageLayout("History", "Follow active scans and open recently filed documents.");
        queueTitle = (Label)queue.Controls[0];
        queueDescription = (Label)queue.Controls[1];
        var queueBody = BuildQueueView();
        queue.Controls.Add(queueBody);
        queue.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        screens.Add(Screen.Decisions, queue);
        screens.Add(Screen.History, queue);

        var reviewPage = PageLayout("Review", "Inspect the selected pages and approve their document boundaries.");
        neighbors = Action("Neighbor pages…", ShowNeighbors);
        restoreSides = Action("Restore omitted side…", RestoreOmittedSide);
        restoreSides.Visible = false;
        actions.Controls.AddRange([neighbors, restoreSides]);
        reviewPage.Controls.Add(reviewContext);
        reviewPage.Controls.Add(actions);
        reviewPage.Controls.Add(previews);
        reviewPage.Controls.Add(edits);
        reviewPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reviewPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        screens.Add(Screen.Review, reviewPage);
        foreach (var screen in screens.Values.Distinct()) workspace.Controls.Add(screen);
        ShowScreen(Screen.Scan);
    }

    private void ShowScreen(Screen screen)
    {
        activeScreen = screen;
        foreach (var panel in screens.Values.Distinct())
            panel.Visible = ReferenceEquals(panel, screens[screen]);
        foreach (var (key, button) in nav)
        {
            button.BackColor = key == screen ? Color.FromArgb(43, 70, 96) : Rail;
            button.ForeColor = key == screen ? Color.White : Color.FromArgb(206, 222, 237);
        }
        screens[screen].BringToFront();
        if (ShowingQueue)
        {
            queueTitle.Text = screen == Screen.Decisions ? "Needs a decision" : "History";
            queueDescription.Text = screen == Screen.Decisions ? "Review a page boundary, crop, or interrupted scan." :
                "Follow active scans and open recently filed documents.";
            RefreshQueue();
        }
    }

    private static TableLayoutPanel PageLayout(string title, string description)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 19, 22, 10),
            BackColor = Canvas
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 23), ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 0), UseMnemonic = false
        });
        panel.Controls.Add(new Label
        {
            Text = description, AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 4, 0, 18),
            UseMnemonic = false
        });
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return panel;
    }

    private static TableLayoutPanel Card(string title)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Fill,
            BackColor = Color.White, Margin = new Padding(0, 0, 0, 13),
            Padding = new Padding(15, 12, 12, 9)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 12),
            ForeColor = Ink, Margin = new Padding(0, 0, 0, 5), UseMnemonic = false
        });
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return card;
    }

    private static Label MetricValue() => new()
    {
        Text = "0", AutoSize = true, Font = new Font("Segoe UI Semibold", 26), ForeColor = Accent,
        Margin = new Padding(0, 2, 0, 0)
    };
}
