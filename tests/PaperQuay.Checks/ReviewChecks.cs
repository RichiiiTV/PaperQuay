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

        var finished = queue.Batches.Single(batch => batch.FiledCount == 1);
        var output = finished.Documents.Single().Outputs!.Single().FiledPath!;
        var batches = Children(window).OfType<ListView>().Single(list => list.AccessibleName == "Scan batches");
        var review = Button("Review document");

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
        Console.WriteLine($"Review ready in {clock.Elapsed.TotalSeconds:F2} s");

        var moved = output + ".moved";
        File.Move(output, moved);
        try
        {
            SelectFiled();
            review.PerformClick();
            await WaitFor(() => Message(automatic ? "Opened local scan." :
                "The filed PDF is unavailable. Retained original sides are open.") &&
                Button("Save copy…").Enabled && Message(automatic ? "1 / 1" : "1 / 2"),
                "Review document did not fall back to the two retained original sides.");
            if (automatic && !Button("Restore omitted side…").Visible)
                throw new InvalidOperationException("The omitted original side was not available to restore.");
        }
        finally { File.Move(moved, output); }

        Children(window).OfType<Button>().Single(button =>
            button.Text.StartsWith("Needs a decision", StringComparison.Ordinal)).PerformClick();
        if (batches.Items.Cast<ListViewItem>().Any(row => Equals(row.Tag, finished.Id)))
            throw new InvalidOperationException("A finished batch appeared in the decision list.");
        Button("Review next").PerformClick();
        await WaitFor(() => Children(window).OfType<Button>().Any(button =>
                button.Text == "Approve & file" && button.Visible && button.Enabled),
            "Review next did not open the pending exception.");
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
        Console.WriteLine("History reopened the filed scan and the decision list opened the pending exception.");
    }
}
