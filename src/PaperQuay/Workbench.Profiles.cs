using NAPS2.Scan;

namespace PaperQuay;

internal sealed record ScanProfile(string Name, string Folder, string PaperSize, int Dpi, int ColorMode,
    bool Duplex, int Workers, ProcessingOptions Processing, QuickFilingOptions Filing,
    string? ScannerId = null, Driver? ScannerDriver = null, bool UseTwainSettings = false);

internal sealed partial class Workbench
{
    private readonly ComboBox profilePicker = Options(210, "Custom settings");
    private ScanProfile[] savedProfiles = OutputFolder.LoadProfiles();
    private ScanProfile? customProfile;
    private bool applyingProfile;

    private ScanProfile? SelectedProfile => profilePicker.SelectedItem as ScanProfile;

    private Control BuildProfilePicker()
    {
        var line = Row();
        profilePicker.AccessibleName = "Scan profile";
        profilePicker.DisplayMember = nameof(ScanProfile.Name);
        profilePicker.Items.AddRange(savedProfiles);
        var saveProfile = Action("Save profile…", SaveProfile);
        var deleteProfile = Action("Remove profile…", DeleteProfile);
        deleteProfile.Enabled = false;
        profilePicker.SelectedIndexChanged += (_, _) =>
        {
            deleteProfile.Enabled = SelectedProfile is not null;
            if (applyingProfile) return;
            if (SelectedProfile is { } profile)
            {
                customProfile ??= CaptureProfile("");
                ApplyProfile(profile);
                status.Text = $"Loaded {profile.Name}.";
            }
            else if (customProfile is { } custom)
            {
                ApplyProfile(custom);
                customProfile = null;
                status.Text = "Custom scan settings restored.";
            }
        };
        line.Controls.AddRange([Caption("Profile"), profilePicker, saveProfile, deleteProfile]);
        profileControls = line;
        return line;
    }

    private Control? profileControls;

    private ScanProfile CaptureProfile(string name, ScanProfile? fallback = null)
    {
        var scanner = devices.SelectedItem as ScanDevice;
        return new ScanProfile(name, saveFolder, SelectedPaper.Name, ScanDpi, colorMode.SelectedIndex,
            sides.SelectedIndex == 0, workers.SelectedIndex + 1, GetScanProcessingOptions(), GetQuickChoices(),
            scanner?.ID ?? fallback?.ScannerId, scanner?.Driver ?? fallback?.ScannerDriver, twainSettings.Checked);
    }

    private void ApplyProfile(ScanProfile profile)
    {
        applyingProfile = true;
        try
        {
            paperSize.SelectedItem = profile.PaperSize;
            if (paperSize.SelectedIndex < 0) paperSize.SelectedItem = ScanPaper.Default.Name;
            resolution.SelectedIndex = profile.Dpi switch { 400 => 1, 600 => 2, _ => 0 };
            colorMode.SelectedIndex = Math.Clamp(profile.ColorMode, 0, colorMode.Items.Count - 1);
            sides.SelectedIndex = profile.Duplex ? 0 : 1;
            workers.SelectedIndex = Math.Clamp(profile.Workers - 1, 0, workers.Items.Count - 1);
            language.SelectedIndex = profile.Processing.Language switch { "eng" => 1, "deu+eng" => 2, _ => 0 };
            scanStraighten.Checked = profile.Processing.Straighten;
            scanClean.Checked = profile.Processing.Clean;
            scanAutoOrient.Checked = profile.Processing.AutoOrient;
            SelectGrouping(profile.Filing);
            removeBlankPages.Checked = profile.Filing.RemovesEmptyPages;
            skipBlankBacks.Checked = profile.Filing.SkipBlankBacks;
            saveFolder = profile.Folder;
            destinationLabel.Text = saveFolder;
            devices.SelectedItem = devices.Items.OfType<ScanDevice>().FirstOrDefault(device =>
                device.ID == profile.ScannerId && device.Driver == profile.ScannerDriver);
            twainSettings.Checked = profile.UseTwainSettings;
        }
        finally { applyingProfile = false; }
    }

    private Task SaveProfile()
    {
        using var dialog = new Form
        {
            Text = "Save scan profile", ClientSize = new Size(390, 115), Font = Font,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var field = new TextBox { Width = 320, MaxLength = 64, AccessibleName = "Profile name",
            Text = SelectedProfile?.Name ?? "" };
        var controls = Row();
        controls.Dock = DockStyle.Fill;
        controls.Padding = new Padding(12);
        var saveName = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true,
            Enabled = !string.IsNullOrWhiteSpace(field.Text) };
        var cancelName = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        field.TextChanged += (_, _) => saveName.Enabled = !string.IsNullOrWhiteSpace(field.Text);
        controls.Controls.AddRange([field, saveName, cancelName]);
        dialog.Controls.Add(controls);
        dialog.AcceptButton = saveName;
        dialog.CancelButton = cancelName;
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        var name = field.Text.Trim();
        var existing = Array.FindIndex(savedProfiles, profile =>
            name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0 && !Confirm($"Replace the settings saved in {savedProfiles[existing].Name}?"))
            return Task.CompletedTask;
        var next = savedProfiles.ToList();
        var profile = CaptureProfile(name, SelectedProfile);
        if (existing < 0) next.Add(profile);
        else next[existing] = profile;
        customProfile ??= profile with { Name = "" };
        ReplaceProfiles(next, profile);
        return Task.CompletedTask;
    }

    private Task DeleteProfile()
    {
        if (SelectedProfile is { } selected && Confirm($"Remove the scan profile {selected.Name}?"))
            ReplaceProfiles(savedProfiles.Where(profile => profile != selected).ToList(), null);
        return Task.CompletedTask;
    }

    private void ReplaceProfiles(List<ScanProfile> next, ScanProfile? selected)
    {
        try { OutputFolder.SaveProfiles(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = $"Scan profiles could not be saved. {ex.Message}";
            return;
        }
        savedProfiles = next.ToArray();
        applyingProfile = true;
        try
        {
            while (profilePicker.Items.Count > 1) profilePicker.Items.RemoveAt(1);
            profilePicker.Items.AddRange(savedProfiles);
            profilePicker.SelectedIndex = selected is null ? 0 : next.IndexOf(selected) + 1;
        }
        finally { applyingProfile = false; }
        if (selected is null && customProfile is { } custom)
        {
            ApplyProfile(custom);
            customProfile = null;
        }
        status.Text = selected is null ? "Profile removed." : $"Saved {selected.Name}.";
    }
}
