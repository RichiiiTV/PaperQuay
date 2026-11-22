using System.Text.Json;

namespace PaperQuay;

internal static class OutputFolder
{
    private static readonly string PreferenceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PaperQuay");
    private static readonly string Preference = Path.Combine(PreferenceFolder, "save-folder.txt");
    private static readonly string QuickPreference = Path.Combine(PreferenceFolder, "quick-filing.json");
    private static readonly string ProfilesPreference = Path.Combine(PreferenceFolder, "scan-profiles.json");

    public static string? Load(bool mixed = false) => ReadFolder(ProfilePreference(mixed)) ?? ReadFolder(Preference)
        ?? (mixed ? ReadFolder(ProfilePreference(false)) : null);

    private static string? ReadFolder(string preference)
    {
        try
        {
            var folder = File.ReadAllText(preference).Trim();
            return Path.IsPathFullyQualified(folder) ? folder : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Save(string folder, bool mixed = false) => WritePreference(ProfilePreference(mixed), folder);

    private static string ProfilePreference(bool mixed) =>
        Path.Combine(PreferenceFolder, mixed ? "mixed-folder.txt" : "routine-folder.txt");

    public static QuickFilingOptions LoadQuick()
    {
        var defaults = new QuickFilingOptions(DetectDocuments: true);
        try
        {
            return JsonSerializer.Deserialize<QuickFilingOptions>(File.ReadAllText(QuickPreference)) ?? defaults;
        }
        catch (IOException) { return defaults; }
        catch (UnauthorizedAccessException) { return defaults; }
        catch (JsonException) { return defaults; }
    }

    public static void SaveQuick(QuickFilingOptions settings) =>
        WritePreference(QuickPreference, JsonSerializer.Serialize(settings));

    public static ScanProfile[] LoadProfiles()
    {
        try { return ParseProfiles(File.ReadAllText(ProfilesPreference)); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    internal static ScanProfile[] ParseProfiles(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<ScanProfile[]>(json) ?? [])
                .Where(profile => profile is { Name.Length: > 0, Folder.Length: > 0, Filing: not null,
                    Processing: not null } && Path.IsPathFullyQualified(profile.Folder))
                .DistinctBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
    }

    public static void SaveProfiles(IEnumerable<ScanProfile> profiles) =>
        WritePreference(ProfilesPreference, JsonSerializer.Serialize(profiles));

    private static void WritePreference(string path, string value)
    {
        Directory.CreateDirectory(PreferenceFolder);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
