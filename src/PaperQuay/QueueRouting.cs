namespace PaperQuay;

internal sealed record RoutedDocument(int Number, int First, int Last, FilingPlan Plan,
    int[]? ExcludedPages = null, int? DecisionPage = null);

internal static class QueueRouting
{
    public static RoutedDocument[] Route(PreparedPage[] pages, bool duplex, QuickFilingOptions options)
    {
        if (pages.Length == 0) return [];
        var separators = pages.Select((page, index) => options.DetectByContent && page.VerifiedBlank &&
            (!duplex || ((index ^ 1) < pages.Length && pages[index ^ 1].VerifiedBlank))).ToArray();
        var plan = DocumentGroups.Plan(pages.Select((page, index) =>
            new GroupInput(page.Clues with { Blank = page.VerifiedBlank || page.OmitSide },
                !duplex ? SheetSide.Unpaired : index % 2 == 0 ? SheetSide.Front : SheetSide.Back,
                duplex ? index / 2 : index, PageNumberTrusted: page.TrustsPageNumber)).ToArray(),
            automatic: options.DetectByContent, sheetsPerDocument: options.SheetsPerDocument);
        var sections = new List<RoutedDocument>();
        var first = 0;
        int? uncertainBoundary = null;
        for (int index = 1; index <= pages.Length; index++)
        {
            if (index < pages.Length)
            {
                if (!plan[index].Starts) continue;
                if (options.DetectByContent && !separators[index - 1] &&
                    !ReliableBoundary(pages, first, index))
                {
                    uncertainBoundary ??= index + 1;
                    continue;
                }
            }

            var number = sections.Count + 1;
            var excluded = new List<int>();
            var kept = new List<int>();
            for (var position = first; position < index; position++)
                (options.Omits(pages[position]) ? excluded : kept).Add(position);
            var content = kept.Where(position => !pages[position].VerifiedBlank).ToArray();
            var omitted = excluded.Count;
            var oneSheet = content.Select(position => duplex ? position / 2 : position).Distinct().Take(2).Count() <= 1;
            var filing = QuickFiling.Plan(kept.Select(position => pages[position]).ToArray(),
                omitted, options.DetectByContent, number);
            int? decision = uncertainBoundary;
            var marked = kept.FirstOrDefault(position => pages[position].ReviewSide, -1);
            if (marked >= 0)
            {
                decision = marked + 1;
                filing = filing with { Documents = [], ReviewReason = "Possible writing on a folded side. Check it before filing." };
            }
            else if (uncertainBoundary is not null)
                filing = new FilingPlan([], omitted, "Check a document boundary next to weak OCR.");
            else if (filing.ReviewReason is null && options.DetectByContent && !oneSheet &&
                     !HasContinuity(content.Select(position => pages[position]).ToArray()))
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
                kept.Count == 0 || excluded.Count == 0 ? null : excluded.Select(position => position + 1).ToArray(),
                decision));
            first = index;
            uncertainBoundary = null;
        }
        return sections.ToArray();
    }

    private static bool ReliableBoundary(PreparedPage[] pages, int first, int index)
    {
        var preceding = pages[first..index].Where(page => !page.VerifiedBlank && !page.OmitSide).ToArray();
        var current = pages[index];
        if (preceding.LastOrDefault() is { NeedsReview: false } && !current.NeedsReview)
            return true;
        if (current.OcrUnavailable || current.Quality.Confidence < 80 || preceding.Length == 0)
            return false;
        if (TrustsClue(preceding[^1]) && preceding[^1].Clues.EndsDocument &&
            (current.Clues.Number == 1 || current.Clues.LetterOpening || current.Clues.HasHeading))
            return true;
        return current.Clues.Reference is { } reference && preceding.Any(page =>
            TrustsClue(page) && page.Clues.Reference is { } earlier && earlier != reference);
    }

    private static bool TrustsClue(PreparedPage page) =>
        !page.OcrUnavailable && (!page.NeedsReview || page.Quality.Confidence >= 80);

    private static bool HasContinuity(PreparedPage[] pages)
    {
        var reference = pages[0].Clues.Reference;
        var last = pages[^1];
        if (reference is not null && last.Clues.Reference == reference &&
            TrustsClue(pages[0]) && TrustsClue(last) &&
            pages.Skip(1).All(page => !page.OcrUnavailable && !page.Clues.LetterOpening &&
                (page.Clues.Reference is null || page.Clues.Reference == reference)))
            return true;
        if (pages[0].Clues.LetterOpening && last.Clues.LetterClosing &&
            TrustsClue(pages[0]) && TrustsClue(last) &&
            pages.Skip(1).All(page => !page.Clues.LetterOpening && !page.OcrUnavailable))
            return true;
        var anchoredStart = TrustsClue(pages[0]) && pages[0].Clues.Number == 1;
        return TrustsClue(last) && last.Clues.Number == pages.Length &&
            (anchoredStart || (last.Clues.Total == pages.Length && pages.All(TrustsClue))) &&
            pages.Select((page, index) => page.NeedsReview || page.Clues.Number == index + 1 ||
                (!anchoredStart && page.Clues.Number is null)).All(valid => valid);
    }
}
