using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PaperQuay;

internal static class ScanImageEvidence
{
    private const int Grid = 192;

    public static bool IsShowThrough(Bitmap front, Bitmap back)
    {
        var reverse = ReadInk(back, out var reversePaper);
        if (reversePaper < 237 || reverse.Max() > 100) return false;
        var reverseBounds = Bounds(reverse, 7);
        if (reverseBounds.Width < 12 || reverseBounds.Height < 12 ||
            reverse.Count(value => value >= 7) < 65)
            return false;

        var colors = new Color[Grid * Grid];
        var face = ReadInk(front, out var facePaper, colors);
        if (facePaper < 220 || face.Count(value => value >= 40) < 65) return false;
        var smoothFace = Smooth(face, 55);
        var smoothReverse = Smooth(reverse, 4);
        var reference = new int[Grid * Grid];
        var smoothReference = new int[Grid * Grid];
        var bestScore = 0d;
        int[]? best = null;
        var bestX = 0;
        var bestY = 0;
        var bestReflection = 0;
        for (var reflection = 0; reflection < 4; reflection++)
        {
            for (var y = 0; y < Grid; y++)
            for (var x = 0; x < Grid; x++)
            {
                var index = ReflectedIndex(x, y, reflection);
                reference[y * Grid + x] = face[index];
                smoothReference[y * Grid + x] = smoothFace[index];
            }
            var faceBounds = Bounds(reference, 28);
            var offsetX = faceBounds.Left - reverseBounds.Left;
            var offsetY = faceBounds.Top - reverseBounds.Top;
            if (Math.Abs(offsetX) > 10 || Math.Abs(offsetY) > 10 ||
                Math.Abs(faceBounds.Width - reverseBounds.Width) > 11 ||
                Math.Abs(faceBounds.Height - reverseBounds.Height) > 11)
                continue;

            for (var dy = offsetY - 5; dy <= offsetY + 5; dy++)
            for (var dx = offsetX - 5; dx <= offsetX + 5; dx++)
            {
                long dot = 0, faceEnergy = 0, backEnergy = 0;
                for (var y = reverseBounds.Top; y < reverseBounds.Bottom; y += 2)
                for (var x = reverseBounds.Left; x < reverseBounds.Right; x += 2)
                {
                    var a = Sample(smoothReference, x + dx, y + dy);
                    var b = smoothReverse[y * Grid + x];
                    dot += a * b;
                    faceEnergy += a * a;
                    backEnergy += b * b;
                }
                var score = faceEnergy == 0 || backEnergy == 0 ? 0 : dot / Math.Sqrt((double)faceEnergy * backEnergy);
                if (score <= bestScore) continue;
                (bestScore, bestX, bestY, bestReflection) = (score, dx, dy, reflection);
                best = (int[])reference.Clone();
            }
        }
        return bestScore >= 0.85 && best is not null &&
            !HasIndependentInk(back, reversePaper, best, bestX, bestY, colors, bestReflection, bestScore >= 0.90);
    }

    private static int ReflectedIndex(int x, int y, int reflection) => reflection switch
    {
        0 => y * Grid + Grid - 1 - x,
        1 => (Grid - 1 - y) * Grid + x,
        2 => x * Grid + y,
        _ => (Grid - 1 - x) * Grid + Grid - 1 - y
    };

    private static int[] Smooth(int[] input, int threshold)
    {
        var across = new int[Grid * Grid];
        var result = new int[across.Length];
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        for (var dx = -2; dx <= 2; dx++)
            across[y * Grid + x] += Math.Max(0, Sample(input, x + dx, y) - threshold);
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        for (var dy = -2; dy <= 2; dy++)
            result[y * Grid + x] += Sample(across, x, y + dy);
        return result;
    }

    private static bool HasIndependentInk(Bitmap back, int paper, int[] face, int dx, int dy,
        Color[] faceColors, int reflection, bool strongMirror)
    {
        var support = new bool[Grid * Grid];
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        for (var ny = -4; ny <= 4; ny++)
        for (var nx = -4; nx <= 4; nx++)
            support[y * Grid + x] |= face[Math.Clamp(y + dy + ny, 0, Grid - 1) * Grid +
                Math.Clamp(x + dx + nx, 0, Grid - 1)] >= 17;

        const int tileSize = 16;
        var columns = back.Width / tileSize + 2;
        var rows = back.Height / tileSize + 2;
        var marks = new int[columns * rows];
        var overlapping = new int[marks.Length];
        var edgeShadows = strongMirror ? CornerEdgeShadows(back) : [];
        var found = false;
        PaperCorrection.VisitRows(back, false, (y, row) =>
        {
            if (found) return;
            var cellY = y * Grid / back.Height;
            for (var x = 0; x < back.Width; x++)
            {
                var offset = x * 3;
                var luma = Luma(row, offset);
                var red = row[offset + 2];
                var green = row[offset + 1];
                var blue = row[offset];
                var chroma = Chroma(red, green, blue);
                var cellX = x * Grid / back.Width;
                if (luma >= 155 && (paper - luma < 16 || support[cellY * Grid + cellX]) &&
                    (chroma <= 58 || MatchesFrontColor(red, green, blue, luma, chroma,
                        cellX + dx, cellY + dy, faceColors, reflection, strongMirror)))
                    continue;
                if (edgeShadows.Contains(y * back.Width + x))
                    continue;
                if (++marks[(y / tileSize) * columns + x / tileSize] < 14 &&
                    ++overlapping[((y + 8) / tileSize) * columns + (x + 8) / tileSize] < 14)
                    continue;
                found = true;
                return;
            }
        });
        return found;
    }

    private static HashSet<int> CornerEdgeShadows(Bitmap back)
    {
        var depth = Math.Max(1, (int)Math.Round(Math.Min(back.Width, back.Height) * 0.012));
        var span = depth * 2;
        var shadows = new HashSet<int>();
        foreach (var top in new[] { true, false })
        foreach (var left in new[] { true, false })
        {
            int X(int column) => left ? column : back.Width - 1 - column;
            int Y(int row) => top ? row : back.Height - 1 - row;

            var mask = new bool[depth, span];
            var pixels = new int[depth, span];
            for (var y = 0; y < depth; y++)
            for (var x = 0; x < span; x++)
            {
                var pixel = back.GetPixel(X(x), Y(y));
                var luma = (30 * pixel.R + 59 * pixel.G + 11 * pixel.B) / 100;
                pixels[y, x] = luma;
                mask[y, x] = luma < 195 && Chroma(pixel.R, pixel.G, pixel.B) <= 32;
            }

            // A feeder-edge shadow stays thin, touches the lateral edge, and fades before the interior.
            for (var row = 0; row < depth; row++)
            {
                if (!mask[row, 0]) continue;
                var component = new List<Point> { new(0, row) };
                mask[row, 0] = false;
                for (var i = 0; i < component.Count; i++)
                {
                    var point = component[i];
                    Add(point.X - 1, point.Y);
                    Add(point.X + 1, point.Y);
                    Add(point.X, point.Y - 1);
                    Add(point.X, point.Y + 1);
                }
                var right = component.Max(point => point.X);
                var bottom = component.Max(point => point.Y);
                var thickness = bottom - component.Min(point => point.Y) + 1;
                if (right >= span - 1 || bottom >= depth - 1 || thickness > depth / 2 ||
                    right + 1 < thickness * 2 || component.Any(point => pixels[point.Y, point.X] < 120))
                    continue;
                foreach (var point in component)
                    shadows.Add(Y(point.Y) * back.Width + X(point.X));

                void Add(int x, int y)
                {
                    if ((uint)x >= span || (uint)y >= depth || !mask[y, x]) return;
                    mask[y, x] = false;
                    component.Add(new Point(x, y));
                }
            }
        }
        return shadows;
    }

    private static bool MatchesFrontColor(int red, int green, int blue, int luma, int chroma,
        int centerX, int centerY, Color[] colors, int reflection, bool strongMirror)
    {
        var first = red - green;
        var second = green - blue;
        var third = blue - red;
        var energy = first * first + second * second + third * third;
        // Color needs a tighter local match than the silhouette used for pale grayscale marks.
        for (var y = Math.Max(0, centerY - 2); y <= Math.Min(Grid - 1, centerY + 2); y++)
        for (var x = Math.Max(0, centerX - 2); x <= Math.Min(Grid - 1, centerX + 2); x++)
        {
            var printed = colors[ReflectedIndex(x, y, reflection)];
            var printedChroma = Chroma(printed.R, printed.G, printed.B);
            var printedLuma = (30 * printed.R + 59 * printed.G + 11 * printed.B) / 100;
            if (printedChroma < chroma || printedLuma > luma - 8)
                continue;
            var a = printed.R - printed.G;
            var b = printed.G - printed.B;
            var c = printed.B - printed.R;
            var dot = a * first + b * second + c * third;
            var clipped = strongMirror && luma >= 225 && Math.Max(red, Math.Max(green, blue)) >= 250 &&
                printedChroma >= chroma + 12 && printedLuma <= luma - 26;
            if (dot >= (clipped ? 0.94 : 0.97) * Math.Sqrt((double)energy * (a * a + b * b + c * c)))
                return true;
        }
        return false;
    }

    private static int Chroma(int red, int green, int blue) =>
        Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));

    public static Rectangle SuggestCrop(Bitmap image, out bool reviewBeforeFiling)
    {
        reviewBeforeFiling = false;
        var ink = ReadInk(image, out var paper);
        if (paper < 218) return Rectangle.Empty;
        var edgeBounds = PaperEdges(ink);
        if (!edgeBounds.IsEmpty)
        {
            var edgeCrop = Rectangle.FromLTRB(edgeBounds.Left * image.Width / Grid, edgeBounds.Top * image.Height / Grid,
                (edgeBounds.Right * image.Width + Grid - 1) / Grid,
                (edgeBounds.Bottom * image.Height + Grid - 1) / Grid);
            edgeCrop.Inflate(image.Width / Grid * 2, image.Height / Grid * 2);
            edgeCrop.Intersect(new Rectangle(0, 0, image.Width, image.Height));
            reviewBeforeFiling = true;
            return edgeCrop;
        }
        var visible = Bounds(ink, 30);
        if (visible.Width < 10 || visible.Height < 10 || (long)visible.Width * visible.Height > Grid * Grid * 0.34 ||
            ink.Count(value => value >= 36) < 80)
            return Rectangle.Empty;

        int left = image.Width, top = image.Height, right = 0, bottom = 0;
        PaperCorrection.VisitRows(image, false, (y, row) =>
        {
            for (var x = 0; x < image.Width; x++)
            {
                var offset = x * 3;
                if (paper - Luma(row, offset) < 10 &&
                    Math.Max(row[offset], Math.Max(row[offset + 1], row[offset + 2])) -
                    Math.Min(row[offset], Math.Min(row[offset + 1], row[offset + 2])) < 14)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = y + 1;
            }
        });
        if (right <= left || bottom <= top) return Rectangle.Empty;
        var crop = Rectangle.FromLTRB(left, top, right, bottom);
        var margin = Math.Max(8, (int)(Math.Min(image.Width, image.Height) * 0.018));
        crop.Inflate(margin, margin);
        crop.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        var fraction = (long)crop.Width * crop.Height / (double)((long)image.Width * image.Height);
        if (fraction > 0.48) return Rectangle.Empty;
        var edge = Grid / 16;
        reviewBeforeFiling = fraction <= 0.30 &&
            (visible.Left <= edge || visible.Right >= Grid - edge) &&
            (visible.Top <= edge || visible.Bottom >= Grid - edge);
        return crop;
    }

    private static Rectangle PaperEdges(int[] ink)
    {
        if (ink[0] < 65 || ink[Grid - 1] < 65 ||
            ink[(Grid - 1) * Grid] < 65 || ink[^1] < 65)
            return Rectangle.Empty;
        var light = ink.Select(value => value < 45 ? 1 : 0).ToArray();
        var bounds = Bounds(light, 1);
        if (bounds.Width < Grid / 8 || bounds.Height < Grid / 8 ||
            bounds.Width * bounds.Height > Grid * Grid * 0.85)
            return Rectangle.Empty;
        var count = light.Sum();
        return count >= bounds.Width * bounds.Height * 0.72 ? bounds : Rectangle.Empty;
    }

    private static int[] ReadInk(Bitmap image, out int paper, Color[]? colors = null)
    {
        using var thumbnail = new Bitmap(Grid, Grid, PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(thumbnail))
        {
            drawing.Clear(Color.White);
            drawing.InterpolationMode = InterpolationMode.HighQualityBilinear;
            drawing.DrawImage(image, 0, 0, Grid, Grid);
        }
        var levels = new int[Grid * Grid];
        var histogram = new int[256];
        PaperCorrection.VisitRows(thumbnail, false, (y, row) =>
        {
            for (var x = 0; x < Grid; x++)
            {
                var level = Luma(row, x * 3);
                var index = y * Grid + x;
                levels[index] = level;
                if (colors is not null)
                    colors[index] = Color.FromArgb(row[x * 3 + 2], row[x * 3 + 1], row[x * 3]);
                histogram[level]++;
            }
        });
        paper = 255;
        for (var remaining = Grid * Grid / 10; paper > 0 && (remaining -= histogram[paper]) > 0; paper--) { }
        for (var index = 0; index < levels.Length; index++)
            levels[index] = Math.Max(0, paper - levels[index]);
        return levels;
    }

    private static Rectangle Bounds(int[] map, int threshold)
    {
        int left = Grid, top = Grid, right = -1, bottom = -1;
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        {
            if (map[y * Grid + x] < threshold) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static int Sample(int[] map, int x, int y) =>
        (uint)x < Grid && (uint)y < Grid ? map[y * Grid + x] : 0;

    private static int Luma(byte[] row, int offset) =>
        (11 * row[offset] + 59 * row[offset + 1] + 30 * row[offset + 2]) / 100;
}
