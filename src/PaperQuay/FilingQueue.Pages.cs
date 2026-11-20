using System.Text.Json;
using NAPS2.Images;

namespace PaperQuay;

internal sealed partial class FilingQueue
{
    private const string CachePrefix = "prepared-v";
    private sealed record CachedPage(PreparedPage Page, long ImageBytes, long PdfBytes);

    public async Task PrepareDuringCapture(QueueBatch batch, IAsyncEnumerable<int> sheets,
        IProgress<int> progress, CancellationToken token)
    {
        using var engine = new DocumentEngine();
        using var slots = new SemaphoreSlim(batch.Workers);
        var complete = 0;
        await Parallel.ForEachAsync(sheets, Workers(batch, token), async (first, canceled) =>
            await PrepareSheet(engine, batch, first, slots, (_, page) =>
            {
                page.Dispose();
                progress.Report(Interlocked.Increment(ref complete));
            }, canceled));
    }

    private static ParallelOptions Workers(QueueBatch batch, CancellationToken token) => new()
    {
        MaxDegreeOfParallelism = batch.Workers, CancellationToken = token
    };

    private async Task PrepareSheet(DocumentEngine engine, QueueBatch batch, int first, SemaphoreSlim slots,
        Action<int, PreparedPage> accept, CancellationToken token)
    {
        var count = batch.Duplex ? 2 : 1;
        var cached = Enumerable.Range(first, count).Select(number => ReadPrepared(batch.Id, number)).ToArray();
        var omission = 0;
        var blank = false;
        if (batch.Duplex && batch.Filing.SkipBlankBacks && cached.Any(page => page is null))
        {
            token.ThrowIfCancellationRequested();
            using var front = new Bitmap(Original(batch.Id, first));
            using var back = new Bitmap(Original(batch.Id, first + 1));
            var blankBack = PaperCorrection.IsUniformPale(back);
            var blankFront = !blankBack && PaperCorrection.IsUniformPale(front);
            blank = blankBack || blankFront;
            omission = blankBack ? first + 1 : blankFront ? first :
                ScanImageEvidence.IsShowThrough(front, back) ? first + 1 :
                ScanImageEvidence.IsShowThrough(back, front) ? first : 0;
        }
        async Task PrepareSide(int offset)
        {
            token.ThrowIfCancellationRequested();
            var number = first + offset;
            var page = cached[offset];
            if (page is null)
            {
                if (number == omission)
                    page = new PreparedPage("", "", PageClues.FromText("", true), "", default, 0, true,
                        VerifiedBlank: blank, OmitSide: true);
                else
                {
                    await slots.WaitAsync(token);
                    try { page = await PrepareOriginal(engine, batch, number, token); }
                    finally { slots.Release(); }
                }
                TryCache(batch.Id, number, page);
            }
            accept(number, page);
        }
        if (count == 1) await PrepareSide(0);
        else await Task.WhenAll(PrepareSide(0), PrepareSide(1));
    }

    private async Task<PreparedPage> PrepareOriginal(DocumentEngine engine, QueueBatch batch, int page,
        CancellationToken token)
    {
        await foreach (var image in engine.Import(Original(batch.Id, page)))
        {
            using (image)
                return await engine.Prepare(image, null, batch.Processing, token);
        }
        throw new InvalidDataException($"Queued page {page} could not be read.");
    }

    private string PreparedPath(Guid id, int number, int version) =>
        Path.Combine(Folder(id), $"{CachePrefix}{version}-{number:D6}");

    public PreparedPage? ReadPrepared(Guid id, int number, bool allowOlder = false)
    {
        var path = PreparedPath(id, number, 2);
        if (allowOlder && !File.Exists(path + ".json"))
            path = PreparedPath(id, number, 1);
        if (!File.Exists(path + ".json")) return null;
        try
        {
            var cached = JsonSerializer.Deserialize<CachedPage>(File.ReadAllText(path + ".json"), Json);
            var page = cached?.Page;
            if (cached is null || page?.Clues is null || (!page.OmitSide &&
                (cached.ImageBytes <= 0 || cached.PdfBytes <= 0 || new FileInfo(path + ".png").Length != cached.ImageBytes ||
                 new FileInfo(path + ".pdf").Length != cached.PdfBytes)))
                return null;
            return page with
            {
                Image = page.OmitSide ? "" : path + ".png", Pdf = page.OmitSide ? "" : path + ".pdf", OwnsFiles = false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void TryCache(Guid id, int number, PreparedPage page)
    {
        var path = PreparedPath(id, number, 2);
        var pending = path + ".tmp";
        try
        {
            // Publish the metadata last so an interrupted write will be prepared again.
            File.Delete(path + ".json");
            if (!page.OmitSide)
            {
                File.Copy(page.Image, path + ".png", overwrite: true);
                File.Copy(page.Pdf, path + ".pdf", overwrite: true);
            }
            var cached = new CachedPage(page with { Image = "", Pdf = "" },
                page.OmitSide ? 0 : new FileInfo(path + ".png").Length,
                page.OmitSide ? 0 : new FileInfo(path + ".pdf").Length);
            File.WriteAllText(pending, JsonSerializer.Serialize(cached, Json));
            File.Move(pending, path + ".json", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { TryDelete(pending); }
    }
}
