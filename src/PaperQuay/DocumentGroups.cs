using System.Text.RegularExpressions;

namespace PaperQuay;

internal enum SheetSide { Unpaired, Front, Back }

internal sealed record PageClues(int? Number, int? Total, string? Reference, bool HasHeading, bool Blank,
    string? DocumentType = null, bool LetterOpening = false, bool LetterClosing = false)
{
    private static readonly Regex PageNumber = new(
        @"^\s*(?:(?:.{1,90}\s+[|·]\s+)?(?:Seite|Page|Blatt|Pg\.)\s*(?<number>\d{1,3})(?:\s*(?:von|of|/)\s*(?<total>\d{1,3}))?(?:\s+[|·]\s+.{1,90})?|(?<number>\d{1,3})\s+(?:of|von|/)\s+(?<total>\d{1,3}))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FooterNumber = new(
        @"^(?<number>\d{1,3})\s*/\s*(?<total>\d{1,3})$", RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceNumber = new(
        @"\b(?:(?<kind>Rechnung|Beleg|Gutschrift|Lieferschein|Angebot|Bestell(?:ung)?|Auftragsbestätigung)(?:s?nummer|(?:s?[- ]+|s?)nr\.?)|(?<kind>Invoice|Receipt|Credit\s+note|Delivery\s+note|Purchase\s+order|Quotation)\s+(?:number|no\.?|#))\s*[:#]?\s*(?<id>[A-Z0-9][A-Z0-9/._-]{1,29})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(
        @"^\s*(?:Rechnung|Musterrechnung|Gutschrift|Lieferschein|Quittung|Kontoauszug|Bescheid|Mahnung|Angebot|Bestellung|Auftragsbestätigung|Mitteilung|Invoice|Receipt|Credit note|Delivery note|Bank statement|Payment reminder|Purchase order|Quotation|Order confirmation)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Opening = new(
        @"^(?:Sehr geehrte[nr]?|Guten Tag|Dear)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Closing = new(
        @"^(?:Mit (?:freundlichen|besten) Grü(?:ß|ss)en|Freundliche Grü(?:ß|ss)e|(?:Kind|Best) regards|(?:Yours )?(?:sincerely|faithfully))[\s,.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool EndsDocument => Number is not null && Number == Total ||
        LetterClosing && (Number is null || Total is null);

    public static PageClues FromText(string text, bool looksBlank)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pageNumbers = lines.Take(6).Concat(lines.TakeLast(7))
            .Select(line => PageNumber.Match(line))
            .Concat(lines.TakeLast(2).Select(line => FooterNumber.Match(line))).Where(match => match.Success)
            .Select(match => (
                int.Parse(match.Groups["number"].Value),
                int.TryParse(match.Groups["total"].Value, out var count) ? (int?)count : null))
            .Where(value => value.Item1 > 0 && (value.Item2 is null || value.Item1 <= value.Item2))
            .Distinct().ToArray();
        var distinctNumbers = pageNumbers.Select(value => value.Item1).Distinct().ToArray();
        var distinctTotals = pageNumbers.Select(value => value.Item2).OfType<int>().Distinct().ToArray();
        int? number = distinctNumbers.Length == 1 && distinctTotals.Length <= 1 ? distinctNumbers[0] : null;
        int? total = number is not null && distinctTotals.Length == 1 ? distinctTotals[0] : null;
        var header = lines.Take(16).ToArray();
        var heading = header.Take(9).Select(line => Heading.Match(line)).FirstOrDefault(match => match.Success);
        var reference = ReferenceNumber.Matches(string.Join(" ", header)).Cast<Match>()
            .Select(match => (Id: match.Groups["id"].Value.TrimEnd('.', '_', '-'), Kind: match.Groups["kind"].Value))
            .Where(value => value.Id.Count(char.IsDigit) >= 2)
            .OrderByDescending(value => heading?.Value.EndsWith(value.Kind, StringComparison.OrdinalIgnoreCase) == true ||
                heading?.Value.Equals("Bestellung", StringComparison.OrdinalIgnoreCase) == true &&
                value.Kind.Equals("Bestell", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Id).FirstOrDefault();
        return new PageClues(number, total,
            reference?.ToUpperInvariant(), heading is not null, looksBlank && !text.Any(char.IsLetterOrDigit),
            heading?.Value.Trim().ToUpperInvariant(), lines.Take(28).Any(Opening.IsMatch),
            lines.TakeLast(18).Any(Closing.IsMatch));
    }
}

internal sealed record GroupInput(PageClues Clues, SheetSide Side = SheetSide.Unpaired,
    int Sheet = 0, bool? BreakBefore = null, bool PageNumberTrusted = true);
internal sealed record GroupAssignment(int Number, bool Starts, string Reason);

internal static class DocumentGroups
{
    public static GroupAssignment[] Plan(IReadOnlyList<GroupInput> pages, bool automatic = true, int sheetsPerDocument = 0)
    {
        var result = new GroupAssignment[pages.Count];
        var inferredStarts = automatic && sheetsPerDocument <= 0 ? InferNumberedStarts(pages) : [];
        var group = 0;
        string? reference = null;
        string? documentType = null;
        PageClues? lastContent = null;
        PageClues? previousContent = null;
        var separator = false;
        var letterStarted = false;
        var previousNumberedEnd = false;
        var sheet = 0;
        for (var index = 0; index < pages.Count; index++)
        {
            var input = pages[index];
            var clues = input.Clues;
            var sameReference = reference is not null && reference == clues.Reference;
            var possibleFront = !clues.Blank && (input.Side != SheetSide.Back || index == 0 ||
                pages[index - 1].Sheet != input.Sheet || pages[index - 1].Clues.Blank);
            var reason = index == 0 ? "First page" : "";
            var newSheet = index == 0 || input.Sheet != pages[index - 1].Sheet;
            if (newSheet) sheet++;

            if (index > 0 && input.BreakBefore is true)
                reason = "Manual split";
            else if (sheetsPerDocument > 0 && index > 0 && input.BreakBefore is null && newSheet &&
                     (sheet - 1) % sheetsPerDocument == 0)
                reason = "Fixed sheet count";
            else if (automatic && sheetsPerDocument <= 0 && index > 0 && input.BreakBefore is null && possibleFront &&
                     lastContent is not null && separator)
                reason = "After blank separator";
            else if (automatic && sheetsPerDocument <= 0 && index > 0 && input.BreakBefore is null && possibleFront && lastContent is not null &&
                     clues.Number is null or 1)
            {
                var differentHeading = documentType is not null && clues.DocumentType is not null &&
                    documentType != clues.DocumentType;
                var completed = previousContent?.EndsDocument == true;
                if (clues.LetterOpening && completed)
                    reason = "Letter opening after a document ending";
                else if (!sameReference && inferredStarts.Contains(index))
                    reason = "Start inferred from a later page number";
                else if (!sameReference && previousNumberedEnd)
                    reason = "After the last numbered page";
                else if (!sameReference && letterStarted && clues.LetterOpening && clues.HasHeading)
                    reason = "New letter opening";
                else if (!sameReference && clues.Number == 1 && (lastContent.Number > 1 || completed ||
                    (lastContent.Number is not null && lastContent.Number == lastContent.Total) ||
                    (clues.Total is not null && lastContent.Total is null) ||
                    (clues.Total is not null && lastContent.Total is not null && clues.Total != lastContent.Total) ||
                    (reference is not null && clues.Reference is not null) ||
                    differentHeading))
                    reason = "Page numbering restarted";
                else if (!sameReference && reference is not null && clues.Reference is not null)
                    reason = "New document reference";
                else if (!sameReference && clues.HasHeading && (completed ||
                             (lastContent.Number is not null && lastContent.Number == lastContent.Total)) &&
                         documentType != clues.DocumentType)
                    reason = "New heading after the final page";
            }

            if (reason.Length != 0)
            {
                group++;
                lastContent = null;
                previousContent = null;
                reference = null;
                documentType = null;
                separator = false;
                letterStarted = false;
                previousNumberedEnd = false;
            }
            var cue = reason.Length != 0 ? reason :
                input.BreakBefore is false ? "Manual join" :
                clues.Blank ? "Empty page" : "";
            result[index] = new GroupAssignment(group, reason.Length != 0, cue);
            if (clues.Blank)
            {
                if (input.Side == SheetSide.Unpaired || (input.Side == SheetSide.Back && index > 0 &&
                    pages[index - 1].Side == SheetSide.Front && pages[index - 1].Sheet == input.Sheet &&
                    pages[index - 1].Clues.Blank))
                    separator = true;
                continue;
            }
            separator = false;
            letterStarted |= clues.LetterOpening;
            previousContent = clues;
            previousNumberedEnd = input.PageNumberTrusted && clues.Number is not null && clues.Number == clues.Total;
            reference ??= clues.Reference;
            documentType ??= clues.DocumentType;
            // Side B can contain a real continuation page, but a blank back cannot reset numbering.
            if (lastContent is null || clues.Number is not null ||
                (lastContent.Number is null && clues.DocumentType is not null))
                lastContent = clues;
        }
        return result;
    }

    private static HashSet<int> InferNumberedStarts(IReadOnlyList<GroupInput> pages)
    {
        var inferred = new HashSet<int>();
        var content = new List<int>();
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page.Clues.Blank) continue;
            content.Add(index);
            if (!page.PageNumberTrusted || page.Clues.Number is not >= 2 || page.Clues.Number != page.Clues.Total)
                continue;
            var length = page.Clues.Number.Value;
            if (length > content.Count) continue;
            var start = content.Count - length;
            if (Enumerable.Range(0, length - 1).All(offset =>
            {
                var earlier = pages[content[start + offset]];
                return earlier.Clues.Number is null || !earlier.PageNumberTrusted ||
                    (earlier.Clues.Number == offset + 1 && (earlier.Clues.Total is null || earlier.Clues.Total == length));
            })) inferred.Add(content[start]);
        }
        return inferred;
    }
}
