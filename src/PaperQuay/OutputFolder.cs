using System.Text.Json;

namespace PaperQuay;

internal static class OutputFolder
{
    private static readonly string PreferenceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PaperQuay");
    private static readonly string Preference = Path.Combine(PreferenceFolder, "save-folder.txt");
    private static readonly string QuickPreference = Path.Combine(PreferenceFolder, "quick-filing.json");

    public static string? Load(bool mixed = false) => ReadFolder(ProfilePreference(mixed)) ?? ReadFolder(Preference);

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
        try
        {
            return JsonSerializer.Deserialize<QuickFilingOptions>(File.ReadAllText(QuickPreference)) ?? new();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }

    public static void SaveQuick(QuickFilingOptions settings) =>
        WritePreference(QuickPreference, JsonSerializer.Serialize(settings));

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

    public static void Export(string folder, PreparedPage[][] documents, CancellationToken token)
    {
        var prefix = $"Scan {DateTime.Now:yyyy-MM-dd HHmmss-fff}";
        var session = Guid.NewGuid().ToString("N");
        var staged = new List<(string Temporary, string Final)>();
        var published = 0;
        try
        {
            for (int i = 0; i < documents.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var temporary = Path.Combine(folder, $".scan-{session}-{i + 1:D2}.tmp");
                var final = Path.Combine(folder, $"{prefix} - {i + 1:D2}.pdf");
                staged.Add((temporary, final));
                DocumentEngine.Merge(temporary, documents[i], token);
            }
            token.ThrowIfCancellationRequested();
            foreach (var (temporary, final) in staged)
            {
                File.Move(temporary, final);
                published++;
            }
        }
        catch (Exception ex) when (published > 0)
        {
            throw new IOException($"Saved {published} of {documents.Length} PDFs with prefix {prefix}. " +
                $"The remaining PDFs could not be published. {ex.Message}", ex);
        }
        finally
        {
            for (var i = published; i < staged.Count; i++)
            {
                // The destination may be offline; keep the original error and leave partial files as .tmp.
                try { File.Delete(staged[i].Temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
