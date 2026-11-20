using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PaperQuay;

internal sealed class PaperCorrection
{
    private const int GridSize = 8;
    private readonly int[,,] background = new int[GridSize, GridSize, 3];
    public bool LooksBlank { get; }
    public bool IsPaper { get; }
    public bool HasDenseGraphics { get; }
    public bool FaintText { get; }
    public long PdfQuality { get; }

    public PaperCorrection(Bitmap source)
    {
        var scale = Math.Min(1d, 650d / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)(source.Width * scale));
        int height = Math.Max(1, (int)(source.Height * scale));
        using var sample = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var counts = new int[GridSize, GridSize, 3, 256];
        var totals = new int[GridSize, GridSize];
        int inspected = 0, light = 0, strongColor = 0, marks = 0, faint = 0;
        VisitRows(sample, false, (y, row) =>
        {
            if (y < height / 32 || y >= height - height / 32)
                return;
            var tileY = y * GridSize / height;
            for (int x = width / 32; x < width - width / 32; x++)
            {
                int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                int minimum = Math.Min(b, Math.Min(g, r)), maximum = Math.Max(b, Math.Max(g, r));
                int luma = (11 * b + 59 * g + 30 * r) / 100;
                inspected++;
                if (maximum - minimum > 43) strongColor++;
                if (luma >= 170) light++;
                if (luma < 155 || maximum - minimum > 37) marks++;
                if (luma is >= 155 and < 206 && maximum - minimum <= 25) faint++;
                var tileX = x * GridSize / width;
                counts[tileY, tileX, 0, b]++;
                counts[tileY, tileX, 1, g]++;
                counts[tileY, tileX, 2, r]++;
                totals[tileY, tileX]++;
            }
        });

        IsPaper = inspected >= 256 && light >= inspected * 0.63 && strongColor <= inspected * 0.18;
        HasDenseGraphics = inspected >= 256 && marks >= inspected * 0.23;
        FaintText = IsPaper && !HasDenseGraphics && faint > Math.Max(60, marks * 2);
        PdfQuality = FaintText ? 92L :
            !IsPaper || HasDenseGraphics || strongColor > Math.Max(6, inspected / 1200) ? 89L : 84L;
        for (int y = 0; y < GridSize; y++)
        for (int x = 0; x < GridSize; x++)
        for (int channel = 0; channel < 3; channel++)
        {
            if (totals[y, x] == 0)
            {
                background[y, x, channel] = 255;
                continue;
            }
            var remaining = Math.Max(1, (totals[y, x] + 9) / 10);
            var value = 255;
            while (value > 0 && (remaining -= counts[y, x, channel, value]) > 0)
                value--;
            background[y, x, channel] = Math.Clamp(value, 170, 255);
        }
        if (IsPaper && marks <= Math.Max(5, inspected / 35000))
        {
            // A light pencil mark can be darker than its local paper and still be above the global cutoff.
            var faintMarks = 0;
            VisitRows(sample, false, (y, row) =>
            {
                if (y < height / 12 || y >= height - height / 12 || faintMarks > 10)
                    return;
                var tileY = y * GridSize / height;
                for (var x = width / 12; x < width - width / 12; x++)
                {
                    var tileX = x * GridSize / width;
                    var paper = 11 * background[tileY, tileX, 0] + 59 * background[tileY, tileX, 1] +
                        30 * background[tileY, tileX, 2];
                    var luma = 11 * row[3 * x] + 59 * row[3 * x + 1] + 30 * row[3 * x + 2];
                    if (paper - luma >= 2400) faintMarks++;
                }
            });
            LooksBlank = faintMarks <= 10;
        }
    }

    public Bitmap Apply(Bitmap source)
    {
        var output = CopyToRgb(source);
        try
        {
            output.SetResolution(source.HorizontalResolution, source.VerticalResolution);

            var xs = Enumerable.Range(0, output.Width).Select(x => Position(x, output.Width)).ToArray();
            var horizontal = new double[GridSize][];
            for (var tileY = 0; tileY < GridSize; tileY++)
            {
                var values = new double[output.Width * 3];
                for (var x = 0; x < output.Width; x++)
                {
                    var (left, fx) = xs[x];
                    for (var c = 0; c < 3; c++)
                        values[3 * x + c] = background[tileY, left, c] * (1 - fx) + background[tileY, left + 1, c] * fx;
                }
                horizontal[tileY] = values;
            }
            VisitRows(output, true, (y, row) =>
            {
                var (top, fy) = Position(y, output.Height);
                var a = horizontal[top];
                var b = horizontal[top + 1];
                for (var i = 0; i < row.Length; i += 3)
                {
                    var chroma = Math.Max(row[i], Math.Max(row[i + 1], row[i + 2])) -
                        Math.Min(row[i], Math.Min(row[i + 1], row[i + 2]));
                    var luma = (11 * row[i] + 59 * row[i + 1] + 30 * row[i + 2]) / 100;
                    var preserve = Math.Clamp((chroma - 18d) / 34, 0, 1) *
                        Math.Clamp((214d - luma) / 58, 0, 1);
                    if (HasDenseGraphics) preserve = Math.Max(preserve, Math.Clamp((200d - luma) / 56, 0, 1));
                    for (var channel = i; channel < i + 3; channel++)
                    {
                        var paper = a[channel] * (1 - fy) + b[channel] * fy;
                        var gain = (int)Math.Clamp(256 + (256 * 252 / paper - 256) * (1 - preserve), 256, 373);
                        row[channel] = (byte)Math.Min(255, (row[channel] * gain + 128) >> 8);
                    }
                }
            });
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public static bool IsUniformPale(Bitmap source)
    {
        if (source.PixelFormat != PixelFormat.Format24bppRgb)
        {
            using var opaque = CopyToRgb(source);
            return IsUniformPale(opaque);
        }
        var reference = source.GetPixel(0, 0);
        if (Math.Min(reference.R, Math.Min(reference.G, reference.B)) < 232) return false;
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, source.PixelFormat);
        try
        {
            var row = new byte[source.Width * 3];
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var i = 0; i < row.Length; i += 3)
                    if (Math.Abs(row[i] - reference.B) > 6 || Math.Abs(row[i + 1] - reference.G) > 6 ||
                        Math.Abs(row[i + 2] - reference.R) > 6)
                        return false;
            }
            return true;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static (int, double) Position(int value, int length)
    {
        var scaled = Math.Clamp(value * (double)GridSize / length - 0.5, 0, GridSize - 1);
        var tile = Math.Min((int)scaled, GridSize - 2);
        return (tile, scaled - tile);
    }

    internal static void VisitRows(Bitmap image, bool write, Action<int, byte[]> visit)
    {
        if (!write && image.PixelFormat != PixelFormat.Format24bppRgb)
        {
            using var opaque = CopyToRgb(image);
            VisitRows(opaque, false, visit);
            return;
        }
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(bounds, write ? ImageLockMode.ReadWrite : ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[image.Width * 3];
            for (int y = 0; y < image.Height; y++)
            {
                var address = data.Scan0 + y * data.Stride;
                Marshal.Copy(address, row, 0, row.Length);
                visit(y, row);
                if (write) Marshal.Copy(row, 0, address, row.Length);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    internal static Bitmap CopyToRgb(Bitmap source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(copy);
            graphics.Clear(Color.White);
            graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }
}
