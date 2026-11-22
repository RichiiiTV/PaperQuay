using System.Numerics;

namespace PaperQuay;

internal static partial class ScanImageEvidence
{
    private static ReverseEvidence InspectPunchedReverse(Bitmap? front, Bitmap back, int[] reverse,
        int paper, bool detectShowThrough)
    {
        if (paper < 243 || reverse.Count(value => value >= 54) > Grid * Grid / 100)
            return ReverseEvidence.Content;
        var width = back.Width;
        var height = back.Height;
        var tone = new byte[checked(width * height)];
        var chroma = new byte[tone.Length];
        PaperCorrection.VisitRows(back, false, (y, row) =>
        {
            for (var x = 0; x < width; x++)
            {
                tone[y * width + x] = (byte)Luma(row, x * 3);
                chroma[y * width + x] = (byte)Chroma(row[x * 3 + 2], row[x * 3 + 1], row[x * 3]);
            }
        });
        var holes = FindPunchPair(tone, width, height, width / 65, width / 14) ??
            FindPunchPair(tone, width, height, width - width / 14, width - width / 65);
        if (holes is null) return ReverseEvidence.Content;

        // Only mask punch-shaped marks when both matching holes are present.
        int ToneAt(int x, int y) => tone[y * width + x];
        var leftRail = FeederRail(ToneAt, width, height, 0, width / 21) ??
            FeederRail(ToneAt, width, height, 0, width / 53, partial: true);
        var rightRail = FeederRail(ToneAt, width, height, width - width / 21, width) ??
            FeederRail(ToneAt, width, height, width - width / 53, width, partial: true);
        var folds = FoldRows(tone, width, height, paper);
        var (firstFold, secondFold) = SlopedFolds(tone, width, height, paper);
        var besideFold = new bool[height];
        var foldHalo = Math.Max(4, height / 80);
        foreach (var y in Enumerable.Range(0, height).Where(y => folds[y]))
        {
            var first = Math.Max(0, y - foldHalo);
            Array.Fill(besideFold, true, first, Math.Min(height, y + foldHalo + 1) - first);
        }
        const int tile = 16;
        var columns = width / tile + 2;
        var localMarks = new int[columns * (height / tile + 2)];
        var overlapping = new int[localMarks.Length];
        var darkMarks = new int[localMarks.Length];
        var edgeX = Math.Max(1, width / 180);
        var edgeY = Math.Max(1, height / 285);
        var clippedY = Math.Max(1, height / 690);
        var railRadius = Math.Max(3, width / 155);
        var foldSpan = Math.Max(7, width / 180);
        bool IsFoldShadow(int x, int y, int level)
        {
            if (!folds[y]) return false;
            if (level >= 234) return true;
            var left = x;
            var right = x;
            // A strong fold trace continues horizontally. A short diagonal mark has no such support.
            while (left > 0 && x - left < foldSpan && tone[y * width + left - 1] <= 226) left--;
            while (right + 1 < width && right - x < foldSpan && tone[y * width + right + 1] <= 226) right++;
            return right - left >= foldSpan;
        }
        bool NearSlopedFold(int x, int y, int radius) =>
            firstFold is not null && Math.Abs(y - firstFold[x]) <= radius ||
            secondFold is not null && Math.Abs(y - secondFold[x]) <= radius;

        bool IsPaleSpeck(int x, int y)
        {
            var count = 0;
            for (var row = Math.Max(0, y - 5); row <= Math.Min(height - 1, y + 5); row++)
            for (var col = Math.Max(0, x - 5); col <= Math.Min(width - 1, x + 5); col++)
            {
                if (tone[row * width + col] < 233) return false;
                if (chroma[row * width + col] >= 28 && ++count > 16) return false;
            }
            return true;
        }
        bool InsidePunch(int x, int y) => holes[0].Contains(x, y) || holes[1].Contains(x, y);

        bool IsArtifact(int x, int y, int level) => InsidePunch(x, y) ||
            (level >= 172 && (IsFoldShadow(x, y, level) || x < edgeX || x >= width - edgeX || y < edgeY || y >= height - edgeY)) ||
            (level >= 216 && (x < width / 95 || x >= width - width / 95)) ||
            (level >= 95 && (y < clippedY || y >= height - clippedY) &&
                tone[Math.Max(0, y - 1) * width + x] >= 210 &&
                tone[Math.Min(height - 1, y + 1) * width + x] >= 210) ||
            (level >= 155 && ((leftRail is not null && Math.Abs(x - leftRail[y]) <= railRadius) ||
                (rightRail is not null && Math.Abs(x - rightRail[y]) <= railRadius)));

        bool[]? reflected = null;
        if (detectShowThrough && front is not null)
        {
            var faded = new bool[Grid * Grid];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var level = tone[y * width + x];
                if (level >= paper - 12 || level < 223 || IsArtifact(x, y, level)) continue;
                faded[y * Grid / height * Grid + x * Grid / width] = true;
            }
            reflected = MatchPaleImprint(ReadInk(front, out _), faded);
        }
        var remaining = 0;
        var hasInk = false;
        var checkFold = false;
        var matchedImprint = false;
        var verticalGap = Math.Max(2, height / 710);
        PaperCorrection.VisitRows(back, false, (y, _) =>
        {
            if (hasInk) return;
            for (var x = 0; x < width; x++)
            {
                var level = tone[y * width + x];
                var color = chroma[y * width + x];
                if (level >= paper - 19 && color < 28) continue;
                if (level >= 242 && color is >= 28 and < 46 && IsPaleSpeck(x, y)) continue;
                if (level <= paper - 20 && color is >= 26 and < 35 && InsidePunch(x, y))
                    continue;
                if (color < 26 && IsArtifact(x, y, level)) continue;
                var nearEdge = x < width / 14 || x >= width - width / 14;
                var foldTolerance = Math.Max(3, height / 600);
                if (level >= 208 && nearEdge) foldTolerance += height / 850;
                if ((level >= 150 && color < 26 || level >= 208 && color < 48) &&
                    NearSlopedFold(x, y, foldTolerance)) continue;
                if (besideFold[y] && level >= 228 && color < 24 &&
                    tone[Math.Max(0, y - verticalGap) * width + x] >= paper - 14 &&
                    tone[Math.Min(height - 1, y + verticalGap) * width + x] >= paper - 14)
                    continue;
                if (level >= 228 && color < 29 && reflected?[y * Grid / height * Grid + x * Grid / width] == true)
                {
                    matchedImprint = true;
                    continue;
                }

                var cell = y / tile * columns + x / tile;
                var overlap = (y + tile / 2) / tile * columns + (x + tile / 2) / tile;
                if ((level >= 225 && color < 40 || ++remaining <= tone.Length / 2800) &&
                    ++localMarks[cell] <= 12 && ++overlapping[overlap] <= 12 &&
                    ((level >= 225 && color < 32) || (level >= 242 && color < 46) || ++darkMarks[cell] <= 8))
                    continue;
                hasInk = true;
                checkFold = (besideFold[y] || NearSlopedFold(x, y, foldHalo)) && level < 228 && color < 26;
                return;
            }
        });
        if (hasInk) return checkFold ? ReverseEvidence.Ambiguous : ReverseEvidence.Content;
        return matchedImprint ? ReverseEvidence.ShowThrough : ReverseEvidence.Empty;
    }

    private static bool[]? MatchPaleImprint(int[] face, bool[] faded)
    {
        var visible = Enumerable.Range(0, faded.Length).Where(i => faded[i]).ToArray();
        if (visible.Length < 60) return null;
        var front = new bool[face.Length];
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            front[y * Grid + x] |= Sample(face, Grid - 1 - x + dx, y + dy) >= 44;
        if (front.Count(value => value) > front.Length / 3) return null;

        bool OverFront(int x, int y) => (uint)x < Grid && (uint)y < Grid && front[y * Grid + x];
        var best = visible.Length * 72 / 100;
        (int X, int Y)? offset = null;
        for (var dy = -7; dy <= 7; dy++)
        for (var dx = -7; dx <= 7; dx++)
        {
            var matches = visible.Count(index => OverFront(index % Grid + dx, index / Grid + dy));
            if (matches > best) (best, offset) = (matches, (dx, dy));
        }
        return offset is { } shift ? Enumerable.Range(0, front.Length)
            .Select(index => OverFront(index % Grid + shift.X, index / Grid + shift.Y)).ToArray() : null;
    }

    private static Rectangle[]? FindPunchPair(byte[] tone, int width, int height, int from, int to)
    {
        var holes = new List<Rectangle>();
        var left = to;
        var right = from;
        var top = 0;
        var bottom = -1;
        var area = 0;
        for (var y = 0; y <= height; y++)
        {
            var count = 0;
            var first = to;
            var last = from;
            if (y < height)
                for (var x = from; x < to; x++)
                {
                    if (tone[y * width + x] >= 178) continue;
                    first = Math.Min(first, x);
                    last = x + 1;
                    count++;
                }
            if (count >= 3)
            {
                if (area == 0) top = y;
                bottom = y;
                area += count;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
            }
            if (area == 0 || (y < height && y - bottom <= Math.Max(1, height / 950))) continue;
            var w = right - left;
            var h = bottom - top + 1;
            if (w >= width / 80 && w <= width / 23 && w >= h && w <= h * 5 && area >= w * h / 4)
                holes.Add(Rectangle.FromLTRB(left, top, right, bottom + 1));
            (left, right, area) = (to, from, 0);
        }
        if (holes is not [var firstHole, var secondHole]) return null;
        var separation = secondHole.Y - firstHole.Y;
        if (separation < height / 5 || separation > height * 43 / 100 ||
            Math.Abs(secondHole.Left - firstHole.Left) > width / 50 ||
            Math.Max(firstHole.Width, secondHole.Width) > Math.Min(firstHole.Width, secondHole.Width) * 1.35)
            return null;

        return holes.Select(hole => Rectangle.FromLTRB(hole.Left - hole.Width / 3, hole.Top - hole.Width,
            hole.Right + hole.Width / 3, hole.Bottom + hole.Width / 4)).ToArray();
    }

    private static int[]? FeederRail(Func<int, int, int> toneAt, int width, int height, int from, int to, bool partial = false)
    {
        var first = Math.Max(1, from);
        var lastColumn = Math.Min(to, width - 1);
        if (first >= lastColumn) return null;
        var points = new List<Point>();
        var step = Math.Max(1, height / 64);
        for (var y = step / 2; y < height; y += step)
        {
            var best = 3 * 244;
            var position = -1;
            var left = toneAt(first - 1, y);
            var middle = toneAt(first, y);
            for (var x = first; x < lastColumn; x++)
            {
                var right = toneAt(x + 1, y);
                var level = left + middle + right;
                (left, middle) = (middle, right);
                if (level >= best) continue;
                (best, position) = (level, x);
            }
            if (position >= 0) points.Add(new Point(position, y));
        }
        var minimum = (height / step) * (partial ? 1 : 3) / 4;
        var tolerance = Math.Max(2, width / 345);
        var bestCount = minimum - 1;
        var origin = 0d;
        var slope = 0d;
        var firstY = 0;
        var lastY = height - 1;
        foreach (var start in points.Where(point => partial || point.Y < height / 3))
        foreach (var end in points.Where(point => partial ? point.Y - start.Y >= height / 4 : point.Y > height * 2 / 3))
        {
            var m = (double)(end.X - start.X) / (end.Y - start.Y);
            if (partial && Math.Abs(m) > 0.05) continue;
            var b = start.X - m * start.Y;
            var matches = points.Count(point => Math.Abs(point.X - (b + m * point.Y)) <= tolerance);
            if (matches <= bestCount) continue;
            (bestCount, origin, slope) = (matches, b, m);
            if (partial)
            {
                firstY = start.Y - step;
                lastY = end.Y + step;
            }
        }
        return bestCount < minimum ? null : Enumerable.Range(0, height)
            .Select(y => y < firstY || y > lastY ? -width : (int)Math.Round(origin + slope * y)).ToArray();
    }

    private static (int[]? First, int[]? Second) SlopedFolds(byte[] tone, int width, int height, int paper)
    {
        var points = new List<Point>();
        for (var y = 0; y < height; y++)
        for (var x = width / 16; x < width * 15 / 16; x += 3)
            if (tone[y * width + x] >= 145 && tone[y * width + x] <= paper - 24)
                points.Add(new Point(x, y));

        // Creases can cross a skewed sheet in short, disconnected segments.
        var votes = new int[height];
        var sections = new uint[height];
        var firstRows = new int[height];
        var lastRows = new int[height];
        var tolerance = Math.Max(2, height / 850);
        var candidates = new (int Votes, int Intercept, double Slope)[height];
        for (var step = -9; step <= 9; step++)
        {
            if (Math.Abs(step) < 2) continue;
            var slope = step / 240d;
            Array.Clear(votes);
            Array.Clear(sections);
            Array.Fill(firstRows, height);
            Array.Clear(lastRows);
            foreach (var point in points)
            {
                var intercept = point.Y - (int)Math.Round(point.X * slope);
                for (var y = Math.Max(0, intercept - tolerance); y <= Math.Min(height - 1, intercept + tolerance); y++)
                {
                    votes[y]++;
                    sections[y] |= 1u << (point.X * 16 / width);
                    firstRows[y] = Math.Min(firstRows[y], point.Y);
                    lastRows[y] = Math.Max(lastRows[y], point.Y);
                }
            }
            for (var y = height / 20; y < height * 19 / 20; y++)
            {
                if (votes[y] < width / 70 || BitOperations.PopCount(sections[y]) < 6 ||
                    lastRows[y] - firstRows[y] < Math.Max(6, height / 250)) continue;
                var center = y + (int)Math.Round(width / 2 * slope);
                if ((uint)center < (uint)height && votes[y] > candidates[center].Votes) candidates[center] = (votes[y], y, slope);
            }
        }
        var first = candidates.MaxBy(candidate => candidate.Votes);
        var second = candidates.Where((candidate, center) => candidate.Votes != 0 &&
            Math.Abs(center - first.Intercept - width / 2 * first.Slope) > height / 8)
            .OrderByDescending(candidate => candidate.Votes).FirstOrDefault();
        int[]? Trace((int Votes, int Intercept, double Slope) line) => line.Votes == 0 ? null :
            Enumerable.Range(0, width).Select(x => line.Intercept + (int)Math.Round(x * line.Slope)).ToArray();
        return (Trace(first), Trace(second));
    }

    private static bool[] FoldRows(byte[] tone, int width, int height, int paper)
    {
        var result = new bool[height];
        var slack = Math.Max(2, height / 650);
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            var dark = 0;
            var left = width;
            var right = 0;
            uint sections = 0;
            uint repeated = 0;
            for (var x = width / 20; x < width * 19 / 20; x++)
            {
                var level = tone[y * width + x];
                if (level >= paper - 10) continue;
                var section = 1u << (x * 16 / width);
                repeated |= sections & section;
                sections |= section;
                left = Math.Min(left, x);
                right = x;
                count++;
                if (level < 170) dark++;
            }
            if (count >= width / 65 && right - left >= width * 42 / 100 &&
                dark <= width / 40 && BitOperations.PopCount(repeated) >= 4)
                Array.Fill(result, true, Math.Max(0, y - slack),
                    Math.Min(height, y + slack + 1) - Math.Max(0, y - slack));
        }
        return result.Count(row => row) <= Math.Max(9, height / 65) ? result : new bool[height];
    }
}
