using NAPS2.Images;

namespace PaperQuay;

internal sealed record ScanPaper(string Name, PageSize Size)
{
    public static ScanPaper Default { get; } = new("A4", PageSize.A4);

    public static IReadOnlyList<ScanPaper> Presets { get; } =
    [
        Default,
        new("A5", PageSize.A5),
        new("A6", new PageSize(105, 148, PageSizeUnit.Millimetre)),
        new("ISO B5", new PageSize(176, 250, PageSizeUnit.Millimetre)),
        new("ISO B6", new PageSize(125, 176, PageSizeUnit.Millimetre)),
        new("ISO B6R", new PageSize(176, 125, PageSizeUnit.Millimetre)),
        new("JIS B5", new PageSize(182, 257, PageSizeUnit.Millimetre)),
        new("JIS B6", new PageSize(128, 182, PageSizeUnit.Millimetre)),
        new("JIS B6R", new PageSize(182, 128, PageSizeUnit.Millimetre)),
        new("US Letter", PageSize.Letter),
        new("US Legal", PageSize.Legal),
        new("US Executive", new PageSize(7.25m, 10.5m, PageSizeUnit.Inch)),
        new("US Statement", new PageSize(5.5m, 8.5m, PageSizeUnit.Inch))
    ];
}
