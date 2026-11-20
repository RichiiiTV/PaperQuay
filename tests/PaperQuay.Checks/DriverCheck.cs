using System.Diagnostics;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using PaperQuay;

internal static class DriverCheck
{
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
        await foreach (var page in engine.Scan(canon, PaperSource.Duplex, 300, timeout.Token))
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
