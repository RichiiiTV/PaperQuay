using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PaperQuay;

internal static class Deskew
{
    // shortcut: estimates small skew in lines of printed text; use a layout-aware detector for photos or rotated layouts.
    public static double CorrectionAngle(Bitmap source)
    {
        const int longestSide = 900;
        var scale = Math.Min(1d, longestSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        using var thumb = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(thumb))
        {
            drawing.Clear(Color.White);
            drawing.DrawImage(source, 0, 0, width, height);
        }

        var bitmap = thumb.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, thumb.PixelFormat);
        var ink = new List<Point>();
        try
        {
            var row = new byte[width * 3];
            for (var y = height / 16; y < height - height / 16; y++)
            {
                Marshal.Copy(bitmap.Scan0 + y * bitmap.Stride, row, 0, row.Length);
                for (var x = width / 16; x < width - width / 16; x++)
                {
                    var offset = x * 3;
                    if (11 * row[offset] + 59 * row[offset + 1] + 30 * row[offset + 2] < 14500)
                        ink.Add(new Point(x, y));
                }
            }
        }
        finally
        {
            thumb.UnlockBits(bitmap);
        }
        if (ink.Count < 100 || ink.Count > width * height / 4)
            return 0;

        var rows = new int[height + 2 * width];
        var offsets = new int[width];
        double Score(double angle)
        {
            Array.Clear(rows);
            var slope = Math.Tan(angle * Math.PI / 180);
            for (var x = 0; x < width; x++)
                offsets[x] = (int)Math.Round((x - width / 2) * slope);
            foreach (var point in ink)
                rows[width + point.Y - offsets[point.X]]++;
            return rows.Sum(count => (double)count * count);
        }

        var baseline = Score(0);
        var bestAngle = 0d;
        var bestScore = baseline;
        void Consider(double angle)
        {
            var score = Score(angle);
            if (score > bestScore)
                (bestAngle, bestScore) = (angle, score);
        }
        for (var step = -12; step <= 12; step++)
            Consider(step / 2d);
        var coarse = bestAngle;
        for (var step = -4; step <= 4; step++)
            Consider(coarse + step / 10d);
        return bestScore > 1.045 * baseline ? -bestAngle : 0;
    }
}
