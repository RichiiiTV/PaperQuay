namespace PaperQuay;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaperQuay", "Queue");
            using var queue = new FilingQueue(path);
            Application.Run(new Workbench(queue, args.Length == 1 ? args[0] : null));
        }
        catch (IOException ex)
        {
            MessageBox.Show($"The local queue could not open. If PaperQuay is already running, use that window.\n\n{ex.Message}",
                "PaperQuay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show($"The local queue needs write access.\n\n{ex.Message}",
                "PaperQuay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
