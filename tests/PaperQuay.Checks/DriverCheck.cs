using System.Diagnostics;
using System.Runtime.InteropServices;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using PaperQuay;

internal static class DriverCheck
{
    public static Task VerifyColdQueueClick(string folder)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var destination = Directory.CreateDirectory(Path.Combine(folder, "output")).FullName;
                using var queue = new FilingQueue(Path.Combine(folder, "queue"));
                using var window = new Workbench(queue, null, destination) { Opacity = 0, ShowInTaskbar = false };
                window.Shown += async (_, _) =>
                {
                    try
                    {
                        IEnumerable<Control> Children(Control root) => root.Controls.Cast<Control>()
                            .SelectMany(child => new[] { child }.Concat(Children(child)));
                        var scanner = Children(window).OfType<ComboBox>()
                            .Single(control => control.AccessibleName == "Scanner connection");
                        if (scanner.SelectedItem is not null) throw new InvalidOperationException("The scanner was already selected.");
                        var scan = Children(window).OfType<Button>().Single(control => control.Text == "Scan & queue");
                        scan.PerformClick();
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        while (!scan.Enabled) await Task.Delay(100, deadline.Token);
                        var batch = queue.Batches.Single();
                        if (batch.Pages == 0 || batch.ScanError is not null)
                            throw new InvalidOperationException($"First click captured {batch.Pages} sides. {batch.ScanError}");
                        Console.WriteLine($"First Scan & queue click discovered the scanner and captured {batch.Pages} sides.");
                        while (queue.Get(batch.Id).Phase is BatchPhase.Queued or BatchPhase.Processing || queue.Get(batch.Id).HasPending)
                            await Task.Delay(100, deadline.Token);
                        var result = queue.Get(batch.Id);
                        Console.WriteLine($"Queue analyzed {result.Pages} sides and skipped {result.Documents.Sum(doc => doc.OmittedBacks)}.");
                        completed.TrySetResult();
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                    finally { Application.ExitThread(); }
                };
                Application.Run(window);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        desktop.SetApartmentState(ApartmentState.STA);
        desktop.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(75));
    }

    public static void VerifyPaperSizes()
    {
        (string Name, decimal Width, decimal Height)[] expected =
        [
            ("A4", 210, 297),
            ("A5", 148, 210),
            ("A6", 105, 148),
            ("ISO B5", 176, 250),
            ("ISO B6", 125, 176),
            ("ISO B6R", 176, 125),
            ("JIS B5", 182, 257),
            ("JIS B6", 128, 182),
            ("JIS B6R", 182, 128),
            ("US Letter", 215.9m, 279.4m),
            ("US Legal", 215.9m, 355.6m),
            ("US Executive", 184.15m, 266.7m),
            ("US Statement", 139.7m, 215.9m)
        ];
        if (!ScanPaper.Presets.Select(paper => paper.Name).SequenceEqual(expected.Select(paper => paper.Name)) ||
            ScanPaper.Default.Name != "A4")
            throw new InvalidOperationException("The paper choices or the A4 default changed.");

        foreach (var (paper, size) in ScanPaper.Presets.Zip(expected))
        foreach (var driver in new[] { Driver.Wia, Driver.Twain })
        foreach (var source in new[] { PaperSource.Feeder, PaperSource.Duplex })
        foreach (var bitDepth in new[] { BitDepth.Color, BitDepth.Grayscale, BitDepth.BlackAndWhite })
        {
            var device = new ScanDevice(driver, "synthetic-device", "Canon DR-C225");
            var options = DocumentEngine.CreateScanOptions(device, source, 300, paper, bitDepth);
            var area = options.PageSize;
            var align = size.Width < 210 ? HorizontalAlign.Center : HorizontalAlign.Right;
            if (area is null || Math.Abs(area.WidthInMm - size.Width) > 0.02m ||
                Math.Abs(area.HeightInMm - size.Height) > 0.02m || options.PageAlign != align ||
                options.Device != device || options.PaperSource != source || options.Dpi != 300 || options.BitDepth != bitDepth ||
                options.ExcludeBlankPages || options.UseNativeUI)
                throw new InvalidOperationException($"Wrong driver options for {size.Name}, {driver}, {source}, {bitDepth}.");
        }

        var other = new ScanDevice(Driver.Wia, "synthetic-device", "Other scanner");
        var a6 = ScanPaper.Presets.Single(paper => paper.Name == "A6");
        if (DocumentEngine.CreateScanOptions(other, PaperSource.Feeder, 600, a6, BitDepth.Color).PageAlign != HorizontalAlign.Right)
            throw new InvalidOperationException("A Canon-specific alignment was applied to another scanner.");
        var twain = new ScanDevice(Driver.Twain, "synthetic-device", "TWAIN scanner");
        var parent = new IntPtr(123);
        var native = DocumentEngine.CreateScanOptions(twain, PaperSource.Duplex, 300, a6, BitDepth.Color, parent);
        if (!native.UseNativeUI || native.DialogParent != parent ||
            DocumentEngine.CreateScanOptions(other, PaperSource.Duplex, 300, a6, BitDepth.Color, parent).UseNativeUI)
            throw new InvalidOperationException("TWAIN driver settings were not passed to the selected driver correctly.");
        Console.WriteLine($"Verified {expected.Length} paper sizes and all three color modes for WIA and TWAIN, simplex and duplex.");
    }

    public static async Task VerifyFeederRecovery(string folder)
    {
        Directory.CreateDirectory(folder);
        var fixture = Path.Combine(folder, "side.png");
        using (var page = new Bitmap(24, 36)) page.Save(fixture, System.Drawing.Imaging.ImageFormat.Png);
        using var engine = new DocumentEngine();
        var closed = 0;
        var starts = 0;
        var received = 0;
        var wiaMultiFeed = new COMException("WIA feed interruption.", unchecked((int)0x80210020));

        async IAsyncEnumerable<ProcessedImage> Capture(int sides, Exception? fault = null)
        {
            try
            {
                for (var side = 0; side < sides; side++)
                    await foreach (var image in engine.Import(fixture))
                        yield return image;
                if (fault is not null) throw fault;
            }
            finally { closed++; }
        }

        bool Reload(string problem, int sheets)
        {
            if (closed != 1 || sheets != 1 || !problem.Contains("two sheets", StringComparison.Ordinal))
                throw new InvalidOperationException("A feeder retry was offered before releasing the scanner or showed the wrong sheet count.");
            return true;
        }

        await foreach (var side in DocumentEngine.CaptureWithFeederRecovery(
            () => ++starts == 1 ? Capture(2, new InvalidOperationException("Driver error", wiaMultiFeed)) : Capture(2),
            duplex: true, Reload, CancellationToken.None))
        {
            using (side) received++;
        }
        if (starts != 2 || received != 4)
            throw new InvalidOperationException("The completed sheets were lost or repeated while resuming a capture.");

        received = 0;
        try
        {
            await foreach (var side in DocumentEngine.CaptureWithFeederRecovery(
                () => Capture(1, new DeviceException("TWAIN error: PaperDoubleFeed")), duplex: true,
                (_, _) => throw new InvalidOperationException("Retried an incomplete duplex sheet."), CancellationToken.None))
            {
                using (side) received++;
            }
            throw new InvalidOperationException("A partial duplex capture was treated as complete.");
        }
        catch (FeederStoppedException ex) when (received == 1 && ex.Message.Contains("Only one side", StringComparison.Ordinal)) { }
        Console.WriteLine("Verified same-batch feeder retry and protection for an incomplete duplex sheet.");
    }

    public static async Task Run(string route, string operation)
    {
        if (route == "app")
        {
            await CheckWorkbench(operation);
            return;
        }
        var imaging = new GdiImageContext();
        using var context = new ScanningContext(imaging);
        context.SetUpWin32Worker();
        var scanner = new ScanController(context);
        var options = new ScanOptions
        {
            Driver = route == "wia" ? Driver.Wia : Driver.Twain,
            PaperSource = PaperSource.Feeder,
            PageSize = PageSize.A4,
            Dpi = 300,
            BitDepth = BitDepth.Color,
            MaxQuality = true,
            ExcludeBlankPages = false
        };
        if (route == "x64")
            options.TwainOptions.Dsm = TwainDsm.NewX64;
        else if (route == "old")
            options.TwainOptions.Dsm = TwainDsm.Old;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var time = Stopwatch.StartNew();
        var devices = await scanner.GetDeviceList(options).WaitAsync(timeout.Token);
        options.Device = devices.FirstOrDefault(device =>
            device.Name.Contains("Canon", StringComparison.OrdinalIgnoreCase) &&
            device.Name.Replace("-", "").Contains("DRC225", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{route}: {(options.Device?.Name ?? "Canon DR-C225 absent")} in {time.Elapsed.TotalSeconds:F2}s");
        if (options.Device == null)
            return;

        time.Restart();
        if (operation == "caps")
        {
            var caps = await scanner.GetCaps(options, timeout.Token).WaitAsync(timeout.Token);
            foreach (var property in caps.GetType().GetProperties())
                Console.WriteLine($"{property.Name}: {property.GetValue(caps)}");
            Console.WriteLine($"Capabilities returned in {time.Elapsed.TotalSeconds:F2}s");
            return;
        }
        if (operation != "scan")
            throw new ArgumentException("Expected caps or scan.", nameof(operation));

        var count = 0;
        scanner.PageStart += (_, _) => Console.WriteLine($"Page start at {time.Elapsed.TotalSeconds:F2}s");
        await foreach (var page in scanner.Scan(options, timeout.Token))
        {
            count++;
            var received = time.Elapsed.TotalSeconds;
            using (page)
            using (var image = imaging.RenderToBitmap(page))
                Console.WriteLine($"Image {count} at {received:F2}s, {image.Width} × {image.Height}, {image.HorizontalResolution:F0} dpi");
        }
        Console.WriteLine($"Scan complete in {time.Elapsed.TotalSeconds:F2}s, {count} image(s)");
        if (count != 1)
            throw new InvalidOperationException("Expected exactly one page from the test sheet.");
    }

    private static async Task CheckWorkbench(string operation)
    {
        using var engine = new DocumentEngine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var devices = await engine.Devices().WaitAsync(timeout.Token);
        foreach (var device in devices)
            Console.WriteLine($"{device.Driver}: {device.Name}");
        var canon = devices.FirstOrDefault(DocumentEngine.IsCanonWia) ??
            throw new InvalidOperationException("The Canon WIA connection was not found by PaperQuay.");
        if (operation != "duplex")
            return;

        var time = Stopwatch.StartNew();
        var count = 0;
        await foreach (var page in engine.Scan(canon, PaperSource.Duplex, 300, ScanPaper.Default, BitDepth.Color, timeout.Token))
        {
            using (page)
            using (var image = engine.Preview(page))
            {
                count++;
                Console.WriteLine($"Side {count} at {time.Elapsed.TotalSeconds:F2}s, {image.Width} × {image.Height}, {image.HorizontalResolution:F0} dpi");
                if (image.Width < 2400 || image.Height < 3400 || Math.Abs(image.HorizontalResolution - 300) > 1)
                    throw new InvalidOperationException("Expected a full A4 image at 300 dpi.");
            }
        }
        Console.WriteLine($"Scan complete in {time.Elapsed.TotalSeconds:F2}s");
        if (count != 2)
            throw new InvalidOperationException($"Expected one sheet with two sides. Received {count}.");
    }
}
