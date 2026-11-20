using System.Diagnostics;
using System.Drawing.Imaging;
using System.Threading.Channels;
using PaperQuay;

internal static class PerformanceCheck
{
    public static async Task Run(string working, bool live = false, int sheets = 3)
    {
        Directory.CreateDirectory(working);
        var destination = Directory.CreateDirectory(Path.Combine(working, "delivered")).FullName;
        using var queue = new FilingQueue(Path.Combine(working, "spool"));
        var batch = queue.Begin(destination, true, new ProcessingOptions(true, true, "deu"), new(), 3);
        using var document = new Bitmap(3308, 4676, PixelFormat.Format24bppRgb);
        using var blank = new Bitmap(document.Width, document.Height, PixelFormat.Format24bppRgb);
        document.SetResolution(400, 400);
        blank.SetResolution(400, 400);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.FromArgb(248, 248, 245));
        using (var graphics = Graphics.FromImage(document))
        using (var heading = new Font("Arial", 19, FontStyle.Bold))
        using (var body = new Font("Arial", 9))
        {
            graphics.Clear(Color.FromArgb(240, 239, 235));
            graphics.DrawString("Musterrechnung AB-592814", heading, Brushes.Black, 280, 270);
            for (var line = 0; line < 24; line++)
                graphics.DrawString("Bitte prüfen Sie die Referenz AB-592814 und den Betrag von 48,90 Euro.",
                    body, Brushes.Black, 280, 660 + 97 * line);
        }
        var clock = Stopwatch.StartNew();
        var channel = Channel.CreateUnbounded<int>();
        var progress = new CaptureProgress();
        var background = live ? Task.Run(() => queue.PrepareDuringCapture(batch, channel.Reader.ReadAllAsync(),
            progress, CancellationToken.None)) : Task.CompletedTask;
        for (var number = 1; number <= sheets * 2; number++)
        {
            queue.StorePage(batch.Id, number, number % 2 == 1 ? document : blank);
            if (live && number % 2 == 0)
            {
                channel.Writer.TryWrite(number - 1);
                if (number < sheets * 2) await Task.Delay(TimeSpan.FromSeconds(1.8));
            }
        }
        queue.FinishCapture(batch.Id);
        channel.Writer.Complete();
        Console.WriteLine($"Spool: {clock.Elapsed.TotalSeconds:F2} s");
        if (live) Console.WriteLine($"Sides ready by end of capture: {progress.Completed} / {sheets * 2}");
        clock.Restart();
        await background;
        await queue.Prepare(batch.Id, CancellationToken.None);
        Console.WriteLine($"Process: {clock.Elapsed.TotalSeconds:F2} s");
        clock.Restart();
        await queue.Transfer(batch.Id, CancellationToken.None);
        Console.WriteLine($"Deliver: {clock.Elapsed.TotalSeconds:F2} s");
        if (queue.Get(batch.Id) is not { FiledCount: 1, HasPending: false, NeedsReview: false } filed ||
            filed.Documents.Single().OmittedBacks != sheets)
            throw new Exception("The performance fixture did not file all sheets and omit every blank back.");
        if (live && (queue.ReadPrepared(batch.Id, 1) is not { PdfImageReady: true } cached ||
            !cached.Text.Contains("AB-592814", StringComparison.Ordinal) ||
            queue.ReadPrepared(batch.Id, 2)?.OmitSide != true))
            throw new Exception("Cached recognition or blank-side evidence was lost.");
    }

    private sealed class CaptureProgress : IProgress<int>
    {
        private int completed;
        public int Completed => Volatile.Read(ref completed);
        public void Report(int value) => Interlocked.Increment(ref completed);
    }
}
