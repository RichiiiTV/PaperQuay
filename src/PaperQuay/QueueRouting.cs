namespace PaperQuay;

internal sealed record RoutedDocument(int Number, int First, int Last, FilingPlan Plan,
    int[]? ExcludedPages = null, int? DecisionPage = null);

internal static class QueueRouting
{
    public static RoutedDocument[] Route(PreparedPage[] pages, bool duplex, QuickFilingOptions options)
    {
        if (pages.Length == 0) return [];
        var sides = pages.Select((_, index) => !duplex ? SheetSide.Unpaired :
            index % 2 == 0 || (options.SkipBlankBacks && pages[index - 1].OmitSide) ? SheetSide.Front : SheetSide.Back).ToArray();
        var separators = pages.Select((page, index) => options.DetectDocuments && page.VerifiedBlank &&
            (!duplex || ((index ^ 1) < pages.Length && pages[index ^ 1].VerifiedBlank))).ToArray();
        var plan = DocumentGroups.Plan(pages.Select((page, index) =>
            new GroupInput(page.Clues with { Blank = page.VerifiedBlank || page.OmitSide },
                sides[index], duplex ? index / 2 : index)).ToArray(),
            automatic: options.DetectDocuments);
        var sections = new List<RoutedDocument>();
        var first = 0;
        int? uncertainBoundary = null;
        for (int index = 1; index <= pages.Length; index++)
        {
            if (index < pages.Length)
            {
                if (!plan[index].Starts) continue;
                var separator = separators[index - 1];
                var preceding = pages[first..index].LastOrDefault(page => !page.VerifiedBlank && !page.OmitSide);
                if (!separator && (preceding is null || preceding.NeedsReview || pages[index].NeedsReview))
                {
                    uncertainBoundary ??= index + 1;
                    continue;
                }
            }

            var number = sections.Count + 1;
            var captured = Enumerable.Range(first, index - first).ToArray();
            bool OmitSide(int position) => options.SkipBlankBacks && pages[position].OmitSide;
            var excluded = captured.Where(position => separators[position] || OmitSide(position)).ToArray();
            var kept = captured.Except(excluded).ToArray();
            var content = kept.Where(position => !pages[position].VerifiedBlank).ToArray();
            var omitted = captured.Count(OmitSide);
            var markedSeparator = (first > 0 && separators[first - 1]) ||
                (index < pages.Length && separators[index - 1]);
            var oneSheet = content.Select(position => duplex ? position / 2 : position).Distinct().Take(2).Count() <= 1;
            var filing = QuickFiling.Plan(kept.Select(position =>
                new QuickPage(pages[position], sides[position], number)).ToArray(), options) with { OmittedBacks = omitted };
            int? decision = uncertainBoundary;
            if (kept.Length == 0)
                filing = new FilingPlan([], 0, "All captured sides look blank.");
            else if (uncertainBoundary is not null)
                filing = new FilingPlan([], omitted, "Check a document boundary next to weak OCR.");
            else if (filing.ReviewReason is null && options.DetectDocuments && !oneSheet &&
                     !markedSeparator && !HasContinuity(content.Select(position => pages[position]).ToArray()))
            {
                decision = content.First(position => (duplex ? position / 2 : position) !=
                    (duplex ? content[0] / 2 : content[0])) + 1;
                filing = new FilingPlan([], omitted, "Check whether this page starts a new document.");
            }
            else if (filing.ReviewReason is null && oneSheet && content.Any(position => pages[position].ReviewCrop))
            {
                decision = content.First(position => pages[position].ReviewCrop) + 1;
                filing = new FilingPlan([], omitted, "Small item detected. Preview the crop or approve the full page.");
            }
            else if (filing.ReviewReason is not null && content.Length > 1)
                decision = content[1] + 1;
            sections.Add(new RoutedDocument(number, first + 1, index, filing,
                kept.Length == 0 || excluded.Length == 0 ? null : excluded.Select(position => position + 1).ToArray(),
                decision));
            first = index;
            uncertainBoundary = null;
        }
        return sections.ToArray();
    }

    private static bool HasContinuity(PreparedPage[] pages)
    {
        var reference = pages[0].Clues.Reference;
        if (reference is not null && pages.All(page => !page.NeedsReview && page.Clues.Reference == reference))
            return true;
        return !pages[0].NeedsReview && pages[0].Clues.Number == 1 &&
            !pages[^1].NeedsReview && pages[^1].Clues.Number == pages.Length &&
            pages.Select((page, index) => page.NeedsReview || page.Clues.Number == index + 1).All(valid => valid);
    }
}
