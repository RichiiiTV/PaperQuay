namespace PaperQuay;

internal sealed record QuickFilingOptions(bool DetectDocuments = false, bool SkipBlankBacks = true,
    bool? RemoveBlankPages = null, int SheetsPerDocument = 0)
{
    public bool RemovesEmptyPages => RemoveBlankPages ?? SkipBlankBacks;
    public bool DetectByContent => DetectDocuments && SheetsPerDocument <= 0;

    public bool Omits(PreparedPage page) => page.VerifiedBlank ? RemovesEmptyPages :
        SkipBlankBacks && page.OmitSide;

    public bool Omits(ReverseEvidence evidence) => evidence == ReverseEvidence.Empty && RemovesEmptyPages ||
        evidence == ReverseEvidence.ShowThrough && SkipBlankBacks;
}
internal sealed record FilingPlan(PreparedPage[][] Documents, int OmittedBacks, string? ReviewReason = null,
    int WeakOcrPages = 0);

internal static class QuickFiling
{
    public static FilingPlan Plan(PreparedPage[] kept, int omitted, bool detectDocuments, int document)
    {
        if (kept.Length == 0)
            return new FilingPlan([], omitted, omitted == 0 ? "The scanner did not return any pages." : null);
        if (kept.All(page => page.Clues.Blank && !page.VerifiedBlank && !page.ReviewSide))
            return new FilingPlan([], omitted, "All pages look blank.");
        if (detectDocuments && HasGroupingConflict(kept.Where(page => !page.VerifiedBlank).ToArray()))
            return new FilingPlan([], omitted,
                $"Document {document} has conflicting page numbers or references. Review the grouping.");
        return new FilingPlan([kept], omitted, WeakOcrPages: kept.Count(page => page.NeedsReview));
    }

    private static bool HasGroupingConflict(PreparedPage[] pages)
    {
        var reliable = pages.Select((page, index) => (page, Number: index + 1))
            .Where(entry => !entry.page.NeedsReview).ToArray();
        return reliable.Select(entry => entry.page.Clues.Reference).OfType<string>().Distinct().Skip(1).Any() ||
            reliable.Any(entry =>
                (entry.page.Clues.Number is { } number && number != entry.Number) ||
                (entry.page.Clues.Total is { } total && total != pages.Length));
    }
}
