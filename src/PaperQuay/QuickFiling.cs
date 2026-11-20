namespace PaperQuay;

internal sealed record QuickFilingOptions(bool DetectDocuments = false, bool SkipBlankBacks = true);
internal sealed record QuickPage(PreparedPage Page, SheetSide Side, int Document);
internal sealed record FilingPlan(PreparedPage[][] Documents, int OmittedBacks, string? ReviewReason = null,
    int WeakOcrPages = 0);

internal static class QuickFiling
{
    public static FilingPlan Plan(IReadOnlyList<QuickPage> batch, QuickFilingOptions options)
    {
        if (batch.Count == 0)
            return new FilingPlan([], 0, "The scanner did not return any pages.");
        if (batch.All(page => page.Page.Clues.Blank))
            return new FilingPlan([], 0, "All pages look blank.");

        var groups = batch.GroupBy(page => options.DetectDocuments ? page.Document : 1).ToArray();
        if (options.DetectDocuments)
        {
            foreach (var group in groups)
            {
                var content = group.Where(page => !page.Page.Clues.Blank).Select(page => page.Page).ToArray();
                if (HasGroupingConflict(content))
                    return new FilingPlan([], 0, $"Document {group.Key} has conflicting page numbers or references. Review the grouping.");
            }
        }

        var omitted = 0;
        var documents = groups.Select(group =>
        {
            var kept = group.Where(page => !options.SkipBlankBacks || !page.Page.OmitSide)
                .Select(page => page.Page).ToArray();
            omitted += group.Count() - kept.Length;
            return kept;
        }).Where(document => document.Length > 0).ToArray();
        return new FilingPlan(documents, omitted, WeakOcrPages: batch.Count(page => page.Page.NeedsReview));
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
