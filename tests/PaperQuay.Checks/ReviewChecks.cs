using NAPS2.Scan;
using PaperQuay;

internal static class ReviewChecks
{
    public static Task Run(string queueFolder)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var queue = new FilingQueue(queueFolder);
                using var window = new Workbench(queue, null) { Opacity = 0, ShowInTaskbar = false };
                window.Shown += async (_, _) =>
                {
                    try
                    {
                        await Verify(window, queue);
                        result.SetResult();
                    }
                    catch (Exception ex) { result.SetException(ex); }
                    finally { Application.ExitThread(); }
                };
                Application.Run(window);
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true };
        desktop.SetApartmentState(ApartmentState.STA);
        desktop.Start();
        return result.Task;
    }

    private static async Task Verify(Workbench window, FilingQueue queue)
    {
        IEnumerable<Control> Children(Control root) =>
            root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Children(child)));

        Button Button(string label) => Children(window).OfType<Button>().Single(button => button.Text == label);
        ComboBox Combo(string name) => Children(window).OfType<ComboBox>().Single(box => box.AccessibleName == name);
        void ShowQueue() => Button("History").PerformClick();
        bool Message(string start) => Children(window).Any(control => control.Text.StartsWith(start, StringComparison.Ordinal));

        async Task WaitFor(Func<bool> ready, string message)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try
            {
                while (!ready()) await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException) { throw new InvalidOperationException(message); }
        }

        window.Size = window.MinimumSize;
        window.PerformLayout();
        var paper = Combo("Paper size");
        var color = Combo("Color mode");
        if (!Combo("Scan profile").Visible || Combo("Queued document mode").Items.Count != 3)
            throw new InvalidOperationException("Scan profiles or fixed sheet counts were missing on the Scan screen.");
        var profiles = Combo("Scan profile");
        var english = new ScanProfile("Audit English", Path.GetTempPath(), "A4", 300, 0, true, 2,
            new ProcessingOptions(false, false, "eng", false), new());
        profiles.Items.Add(english);
        profiles.SelectedItem = english;
        var colorRight = window.PointToClient(color.PointToScreen(new Point(color.Width, 0))).X;
        if (!paper.Visible || !color.Visible || colorRight > window.ClientSize.Width ||
            paper.PointToScreen(Point.Empty).Y != color.PointToScreen(Point.Empty).Y)
            throw new InvalidOperationException("Paper size and Color mode do not fit together at the minimum window size.");

        var scanner = Combo("Scanner connection");
        var twainSettings = Children(window).OfType<CheckBox>().Single(box => box.Text == "Use TWAIN scanner settings");
        scanner.Items.Add(new ScanDevice(Driver.Wia, "wia", "WIA scanner"));
        scanner.Items.Add(new ScanDevice(Driver.Twain, "twain", "TWAIN scanner"));
        scanner.SelectedIndex = 1;
        twainSettings.Checked = true;
        if (!twainSettings.Visible || paper.Enabled || color.Enabled || !Combo("Feeder sides").Enabled)
            throw new InvalidOperationException("The TWAIN settings control did not expose driver settings and retain the side choice.");
        scanner.SelectedIndex = 0;
        if (twainSettings.Visible || !paper.Enabled || !color.Enabled)
            throw new InvalidOperationException("Selecting WIA did not restore PaperQuay's capture controls.");
        scanner.Items.Clear();
        twainSettings.Checked = false;

        var finished = queue.Batches.Single(batch => batch.FiledCount == 1);
        var output = finished.Documents.Single().Outputs!.Single().FiledPath!;
        var batches = Children(window).OfType<ListView>().Single(list => list.AccessibleName == "Scan batches");
        var review = Button("Review document");
        var skippedPages = Children(window).OfType<CheckBox>().Single(box => box.Text == "Show skipped pages");
        var pageRows = Children(window).OfType<ListView>().Single(list =>
            list.AccessibleName == "Pages and suggested documents");

        void SelectFiled()
        {
            ShowQueue();
            batches.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, finished.Id)).Selected = true;
            if (!review.Enabled) throw new InvalidOperationException("A filed document cannot be selected for review.");
        }

        SelectFiled();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        review.PerformClick();
        var automatic = finished.Documents.Single().FromAutomaticPages;
        await WaitFor(() => Message(automatic ? "Opened local scan." : "Filed PDF opened.") &&
            Button("Save copy…").Enabled && Message("1 / 1"),
            "Review document did not open the published one-page PDF as an editable copy.");
        if (Combo("OCR language").SelectedIndex != 1 || !ReferenceEquals(profiles.SelectedItem, english))
            throw new InvalidOperationException("Opening a German review changed the selected English scan profile.");
        Console.WriteLine($"Review ready in {clock.Elapsed.TotalSeconds:F2} s");
        var mode = Combo("Preview mode");
        var corrected = Children(window).OfType<PictureBox>().Single(box => box.AccessibleName == "Corrected page preview");
        var original = Children(window).OfType<PictureBox>().Single(box => box.AccessibleName == "Original page preview");
        if (!corrected.Visible || original.Visible || corrected.Width < 350)
            throw new InvalidOperationException("The corrected page does not use the full preview area by default.");
        var fullWidth = corrected.Width;
        mode.SelectedItem = "Compare";
        if (!original.Visible || !corrected.Visible || corrected.Width >= fullWidth)
            throw new InvalidOperationException("Compare did not show both pages side by side.");
        mode.SelectedItem = "Original";
        if (!original.Visible || corrected.Visible)
            throw new InvalidOperationException("Original did not use the preview area by itself.");
        mode.SelectedItem = "Corrected";
        mode.SelectedItem = "OCR highlights";
        if (!corrected.Visible || original.Visible || !Message("Words with low OCR confidence"))
            throw new InvalidOperationException("OCR highlights did not use the corrected page preview.");
        mode.SelectedItem = "Corrected";

        var moved = output + ".moved";
        File.Move(output, moved);
        try
        {
            SelectFiled();
            review.PerformClick();
            await WaitFor(() => Message(automatic ? "Opened local scan." :
                "The filed PDF is unavailable. Retained original sides are open.") &&
                Button("Save copy…").Enabled && Message("1 / 1"),
                "Review document did not fall back to the two retained original sides.");
            if (automatic && !Button("Restore omitted side…").Visible)
                throw new InvalidOperationException("The omitted original side was not available to restore.");
            if (!automatic)
            {
                if (!skippedPages.Visible || Button("Next").Enabled)
                    throw new InvalidOperationException("A confirmed show-through side was visible by default.");
                skippedPages.Checked = true;
                await WaitFor(() => pageRows.Items.Count == 2 && Button("Next").Enabled,
                    "Show skipped pages did not expose the retained reverse.");
                Button("Next").PerformClick();
                await WaitFor(() => Message("2 / 2") && Children(window).OfType<Button>()
                    .Any(button => button.Text == "Keep reverse" && button.Visible && button.Enabled),
                    "Reopening retained originals brought a duplex show-through side back into the export.");
            }
        }
        finally { File.Move(moved, output); }

        Children(window).OfType<Button>().Single(button =>
            button.Text.StartsWith("Needs a decision", StringComparison.Ordinal)).PerformClick();
        if (batches.Items.Cast<ListViewItem>().Any(row => Equals(row.Tag, finished.Id)))
            throw new InvalidOperationException("A finished batch appeared in the decision list.");
        Button("Review next").PerformClick();
        await WaitFor(() => Children(window).OfType<Button>().Any(button =>
                button.Text == "Approve & file" && button.Visible && button.Enabled) && Message("1 / 2"),
            "Review next did not open the interrupted duplex capture without its show-through side.");
        if (!skippedPages.Visible || !pageRows.Items.Cast<ListViewItem>().Select(row => row.Text).SequenceEqual(["1", "3"]))
            throw new InvalidOperationException("Interrupted duplex review lost the pairing or presented an unprocessed reverse.");
        if (!queue.Batches.Any(batch => batch.NeedsReview) || queue.Get(finished.Id).FiledCount != 1)
            throw new InvalidOperationException("Opening the documents changed their filing status.");
        if (automatic)
        {
            SelectFiled();
            review.PerformClick();
            await WaitFor(() => Message("Opened local scan.") && Button("Restore omitted side…").Visible,
                "Could not reopen the scan to restore the omitted side.");
            using var dialogTimer = new System.Windows.Forms.Timer { Interval = 50 };
            dialogTimer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form =>
                    form.Text == "Restore an omitted side");
                if (dialog is null) return;
                dialogTimer.Stop();
                dialog.DialogResult = DialogResult.OK;
            };
            dialogTimer.Start();
            Button("Restore omitted side…").PerformClick();
            await WaitFor(() => Message("Restored original side") && Button("Save copy…").Enabled &&
                (Message("1 / 2") || Message("2 / 2")), "Restoring the side did not return it to the review.");
        }

        var boundary = queue.Batches.Single(batch => batch.Documents.Any(doc =>
            doc.Reason?.StartsWith("Check whether this page", StringComparison.Ordinal) == true));
        ShowQueue();
        batches.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, boundary.Id)).Selected = true;
        review.PerformClick();
        await WaitFor(() => Button("Split here").Visible && Button("Split here").Enabled &&
                Message("2 / 2") && original.Image is not null && corrected.Image is not null,
            "An uncertain boundary did not open both corrected pages and its inline actions.");
        if (!Equals(mode.SelectedItem, "Page boundary") || !Message("Previous page") || !Message("Current page"))
            throw new InvalidOperationException("The review did not identify the two pages at the boundary.");
        Button("Split here").PerformClick();
        if (pageRows.Items[1].SubItems[1].Text != "2" ||
            queue.Get(boundary.Id).Documents.Single().Draft?.Pages[1].BreakBefore != true)
            throw new InvalidOperationException("Split here did not start a new document.");
        Button("Keep together").PerformClick();
        if (pageRows.Items[1].SubItems[1].Text != "1")
            throw new InvalidOperationException("Keep together did not join the two pages.");
        Button("Move up").PerformClick();
        await WaitFor(() => Message("Moved page to position 1.") && !Button("Move up").Enabled,
            "Moving a page up did not follow the page to its new position.");
        mode.SelectedItem = "Original";
        await WaitFor(() => original.Visible && original.Image is Bitmap bitmap &&
            bitmap.GetPixel(920, 910).B > bitmap.GetPixel(920, 910).R + 40,
            "Reordering did not put the marked page in the first position.");
        Button("Move down").PerformClick();
        await WaitFor(() => Message("Moved page to position 2.") && !Button("Move down").Enabled,
            "Moving the page down did not restore its original position.");
        using var restore = new System.Windows.Forms.Timer { Interval = 50 };
        restore.Tick += (_, _) =>
        {
            if (Application.OpenForms.Cast<Form>().SingleOrDefault(form => form.Text == "Restore an omitted side")
                is not { } dialog) return;
            restore.Stop();
            dialog.DialogResult = DialogResult.OK;
        };
        restore.Start();
        Button("Restore omitted side…").PerformClick();
        await WaitFor(() => Message("3 / 3") && Button("Skip empty page").Enabled,
            "Restoring an empty page did not include it in the document.");
        Button("Skip empty page").PerformClick();
        await WaitFor(() => pageRows.Items.Count == 2 && Button("Split here").Enabled && Message("2 / 2"),
            "Skipping the restored empty page did not return review to the document boundary.");
        Button("Split here").PerformClick();
        Button("Approve & file").PerformClick();
        await WaitFor(() => queue.Get(boundary.Id).FiledCount == 2,
            "The approved split did not result in two filed PDFs.");
        var splitFiles = queue.Get(boundary.Id).Documents.Single().Outputs!;
        foreach (var file in splitFiles)
        {
            using var pdf = PdfSharpCore.Pdf.IO.PdfReader.Open(file.FiledPath!, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            if (pdf.PageCount != 1) throw new InvalidOperationException("The skipped empty page was exported.");
        }
        var secondFile = splitFiles[1].FiledPath!;
        File.Move(secondFile, secondFile + ".moved");
        try
        {
            ShowQueue();
            batches.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, boundary.Id)).Selected = true;
            var documents = Children(window).OfType<ListView>().Single(list => list.AccessibleName == "Documents in selected batch");
            documents.Items[1].Selected = true;
            review.PerformClick();
            await WaitFor(() => Message("The filed PDF is unavailable.") && Button("Save copy…").Enabled && Message("1 / 1"),
                "Reopening a moved split PDF included pages from another document.");
            mode.SelectedItem = "Original";
            await WaitFor(() => original.Visible && original.Image is Bitmap bitmap &&
                bitmap.GetPixel(920, 910).B > bitmap.GetPixel(920, 910).R + 40,
                "The reopened split PDF used the wrong original page.");
        }
        finally { File.Move(secondFile + ".moved", secondFile); }
        var folded = queue.Batches.Single(batch => batch.Documents.Any(doc =>
            doc.Reason?.StartsWith("Possible writing", StringComparison.Ordinal) == true));
        ShowQueue();
        batches.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, folded.Id)).Selected = true;
        review.PerformClick();
        await WaitFor(() => Message("Possible writing") && Children(window).OfType<Button>()
            .Any(button => button.Text == "Skip this side" && button.Visible && button.Enabled),
            "Review did not open directly on the ambiguous folded side.");
        Button("Skip this side").PerformClick();
        await WaitFor(() => pageRows.Items.Count == 1 && skippedPages.Enabled,
            "The folded side was still in the review after being skipped.");
        skippedPages.Checked = true;
        await WaitFor(() => pageRows.Items.Count == 2 && Button("Next").Enabled,
            "The skipped folded side was unavailable for restoration.");
        Button("Next").PerformClick();
        await WaitFor(() => Message("2 / 2") && Children(window).OfType<Button>()
            .Any(button => button.Text == "Restore this side" && button.Visible && button.Enabled),
            "The skipped folded side could not be inspected.");
        Button("Restore this side").PerformClick();
        await WaitFor(() => Button("Approve & file").Enabled, "The restored folded sheet could not be approved.");
        Button("Approve & file").PerformClick();
        await WaitFor(() => queue.Get(folded.Id).FiledCount == 1, "The reviewed folded sheet was not filed.");
        using (var markedPdf = PdfSharpCore.Pdf.IO.PdfReader.Open(foldedOutput(), PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import))
            if (markedPdf.PageCount != 2) throw new InvalidOperationException("The marked side was lost on approval.");
        Console.WriteLine("Verified review of filed scans, page grouping, and keeping an ambiguous folded side.");

        SelectFiled();
        review.PerformClick();
        await WaitFor(() => Children(window).OfType<Button>().Any(button =>
                button.Text == "Save copy…" && button.Visible && button.Enabled),
            "Could not reopen a filed document before clearing history.");
        var parkedPreview = corrected.Image;
        var capturing = queue.Begin(finished.Destination, true, finished.Processing, finished.Filing, finished.Workers);
        ShowQueue();
        var clearHistory = Button("Clear history");
        var confirmedPaths = ClearWithConfirmation(delete: false);
        if (!confirmedPaths.Contains(output) || !File.Exists(output) || batches.Items.Count == 0)
            throw new InvalidOperationException("Canceling the confirmation changed History or its files.");
        confirmedPaths = ClearWithConfirmation(delete: true);
        await WaitFor(() => Message("History cleared.") && clearHistory.Enabled, "History deletion did not complete.");
        if (!clearHistory.Visible || batches.Items.Count != 0 || confirmedPaths.Any(File.Exists))
            throw new InvalidOperationException("History could not be cleared while capturing and reviewing a batch.");
        clearHistory.PerformClick();
        if (!Message("History is already clear."))
            throw new InvalidOperationException("Clear history was unavailable on an empty list.");
        Button("Queue").PerformClick();
        if (clearHistory.Visible || !batches.Items.Cast<ListViewItem>().Any(row => Equals(row.Tag, capturing.Id)))
            throw new InvalidOperationException("The still-capturing batch was lost from Queue.");
        queue.FinishCapture(capturing.Id);
        Children(window).OfType<Button>().Single(button =>
            button.Text.StartsWith("Needs a decision", StringComparison.Ordinal)).PerformClick();
        if (batches.Items.Count == 0 || !Button("Review next").Enabled)
            throw new InvalidOperationException("Pending reviews were lost after clearing history.");
        ShowQueue();
        if (batches.Items.Count != 0)
            throw new InvalidOperationException("Completing a cleared batch brought it back to History.");
        Button("Review").PerformClick();
        if (parkedPreview is null || !ReferenceEquals(parkedPreview, corrected.Image) || !Button("Save copy…").Enabled)
            throw new InvalidOperationException("Clearing history closed an open review.");
        Console.WriteLine("Verified clearing History with pending scans and an open review.");

        string foldedOutput() => queue.Get(folded.Id).Documents.Single().Outputs!.Single().FiledPath!;

        string[] ClearWithConfirmation(bool delete)
        {
            string[] paths = [];
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form => form.Text == "Clear history");
                if (dialog is null) return;
                timer.Stop();
                paths = Children(dialog).OfType<ListBox>().Single().Items.Cast<string>().ToArray();
                Children(dialog).OfType<Button>().Single(button =>
                    button.Text == (delete ? "Delete PDFs and clear" : "Cancel")).PerformClick();
            };
            timer.Start();
            clearHistory.PerformClick();
            return paths;
        }
    }
}
