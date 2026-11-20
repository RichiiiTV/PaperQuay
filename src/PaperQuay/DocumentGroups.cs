using System.Text.RegularExpressions;

namespace PaperQuay;

internal enum SheetSide { Unpaired, Front, Back }

internal sealed record PageClues(int? Number, int? Total, string? Reference, bool HasHeading, bool Blank,
    string? DocumentType = null)
{
    private static readonly Regex PageNumber = new(
        @"^\s*(?:(?:Seite|Page|Blatt|Pg\.)\s*(?<number>\d{1,3})(?:\s*(?:von|of|/)\s*(?<total>\d{1,3}))?|(?<number>\d{1,3})\s+(?:of|von|/)\s+(?<total>\d{1,3}))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceNumber = new(
        @"(?:Rechnungs(?:nummer|[- ]?nr\.?)|Beleg(?:nummer|[- ]?nr\.?)|Gutschrift(?:snummer|[- ]?nr\.?)|Invoice\s+(?:number|no\.?|#)|Receipt\s+(?:number|no\.?|#)|Credit\s+note\s+(?:number|no\.?|#))\s*[:#]?\s*(?<id>[A-Z0-9][A-Z0-9/._-]{1,29})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(
        @"^\s*(?:Rechnung|Musterrechnung|Gutschrift|Lieferschein|Quittung|Kontoauszug|Bescheid|Invoice|Receipt|Credit note|Delivery note|Bank statement)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PageClues FromText(string text, bool looksBlank)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pageNumbers = lines.Take(6).Concat(lines.TakeLast(7))
            .Select(line => PageNumber.Match(line)).Where(match => match.Success)
            .Select(match => (
                int.Parse(match.Groups["number"].Value),
                int.TryParse(match.Groups["total"].Value, out var count) ? (int?)count : null))
            .Where(value => value.Item1 > 0 && (value.Item2 is null || value.Item1 <= value.Item2))
            .Distinct().ToArray();
        var (number, total) = pageNumbers.Length == 1 ? pageNumbers[0] : ((int?)null, (int?)null);
        var header = lines.Take(16).ToArray();
        var reference = ReferenceNumber.Match(string.Join(" ", header)).Groups["id"].Value.TrimEnd('.', '_', '-');
        var heading = header.Take(9).Select(line => Heading.Match(line)).FirstOrDefault(match => match.Success);
        return new PageClues(number, total,
            reference.Count(char.IsDigit) >= 2 ? reference.ToUpperInvariant() : null,
            heading is not null, looksBlank && !text.Any(char.IsLetterOrDigit), heading?.Value.Trim().ToUpperInvariant());
    }
}

internal sealed record GroupInput(PageClues Clues, SheetSide Side = SheetSide.Unpaired,
    int Sheet = 0, bool? BreakBefore = null);
internal sealed record GroupAssignment(int Number, bool Starts, string Reason);

internal static class DocumentGroups
{
    public static GroupAssignment[] Plan(IReadOnlyList<GroupInput> pages, bool automatic = true)
    {
        var result = new GroupAssignment[pages.Count];
        var group = 0;
        string? reference = null;
        PageClues? lastContent = null;
        var separator = false;
        for (var index = 0; index < pages.Count; index++)
        {
            var input = pages[index];
            var clues = input.Clues;
            var sameReference = reference is not null && reference == clues.Reference;
            var possibleFront = input.Side != SheetSide.Back && !clues.Blank;
            var reason = index == 0 ? "First page" : "";

            if (index > 0 && input.BreakBefore is true)
                reason = "Manual split";
            else if (automatic && index > 0 && input.BreakBefore is null && possibleFront &&
                     lastContent is not null && separator)
                reason = "After blank separator";
            else if (automatic && index > 0 && input.BreakBefore is null && possibleFront && lastContent is not null &&
                     !sameReference && clues.Number is null or 1)
            {
                if (clues.Number == 1 && (lastContent.Number > 1 ||
                    (lastContent.Number is not null && lastContent.Number == lastContent.Total) ||
                    (reference is not null && clues.Reference is not null) ||
                    (lastContent.DocumentType is not null && clues.DocumentType is not null &&
                     lastContent.DocumentType != clues.DocumentType)))
                    reason = "Page numbering restarted";
                else if (clues.HasHeading && reference is not null && clues.Reference is not null)
                    reason = "New document reference";
            }

            if (reason.Length != 0)
            {
                group++;
                lastContent = null;
                reference = null;
                separator = false;
            }
            var cue = reason.Length != 0 ? reason :
                input.BreakBefore is false ? "Manual join" :
                clues.Blank ? "Likely blank · kept" : "";
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
            reference ??= clues.Reference;
            // Side B can contain a real continuation page, but a blank back cannot reset numbering.
            if (lastContent is null || clues.Number is not null ||
                (lastContent.Number is null && clues.DocumentType is not null))
                lastContent = clues;
        }
        return result;
    }
}
