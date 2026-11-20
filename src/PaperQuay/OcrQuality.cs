using System.Globalization;

namespace PaperQuay;

internal readonly record struct OcrQuality(int Characters, double Confidence, int HorizontalLines = 0, int VerticalLines = 0)
{
    public bool NeedsReview => Characters < 16 || Confidence < 78;
    public bool WorthRetrying => Characters < 16 || Confidence < 83;
    public bool ClearlyUpright => Characters >= 120 && Confidence >= 94 && HorizontalLines >= 3 && VerticalLines == 0;
    private double Score => Confidence + Math.Min(58, 8 * Math.Log2(1 + Characters));

    public bool ImprovesOn(OcrQuality earlier) => Characters > 0 &&
        (earlier.Characters == 0 ||
         (Characters >= earlier.Characters / 2 && Score >= earlier.Score + 4) ||
         (Characters >= earlier.Characters * 1.5 + 8 && Confidence >= 75 && Score >= earlier.Score - 2));

    public bool SupportsOrientation(OcrQuality earlier) =>
        Characters >= 10 && Confidence >= 80 && HorizontalLines > VerticalLines &&
        (ImprovesOn(earlier) ||
         (earlier.VerticalLines > earlier.HorizontalLines && Characters >= earlier.Characters * 0.7));

    public static OcrQuality FromTsv(string tsv)
    {
        var characters = 0;
        var confidence = 0d;
        var horizontal = 0;
        var vertical = 0;
        foreach (var entry in tsv.Split('\n'))
        {
            var fields = entry.Split('\t', 12);
            if (fields.Length == 12 && fields[0] == "4" &&
                int.TryParse(fields[8], CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(fields[9], CultureInfo.InvariantCulture, out var height) && width > 0 && height > 0)
            {
                if (width > 3d * height) horizontal++;
                if (height > 2d * width) vertical++;
            }
            if (fields.Length != 12 || fields[0] != "5" ||
                !double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                value is < 0 or > 100 || !double.IsFinite(value))
                continue;
            var length = fields[11].Count(char.IsLetterOrDigit);
            characters += length;
            confidence += length * value;
        }
        return new OcrQuality(characters, characters == 0 ? 0 : confidence / characters, horizontal, vertical);
    }
}
