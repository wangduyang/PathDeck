namespace QuickFileDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Each mode owns its WPF Application and message loop in a separate process.
        if (args.Contains("--desktop-module") || args.Contains("--stop"))
        {
            TaskGlass.Program.Main(args.Where(arg => arg != "--desktop-module").ToArray());
            return;
        }
        // Reuse the file module already kept in the background for Win+E and tray navigation.
        if (TryReuseFileModule(args, DesktopModule.EnsureStarted, EventWaitHandle.OpenExisting)) return;
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    internal static bool TryReuseFileModule(string[] args, Action ensureDesktop, Func<string, EventWaitHandle> openNavigation)
    {
        if (args.Contains("--desktop-module") || args.Contains("--stop")) return false;
        if (args.Length == 0 || args.Contains("--file-settings") || args.Contains("--background"))
        {
            try
            {
                using var signal = openNavigation(args.Contains("--file-settings") ? @"Local\PathDeck.Files.Settings" : @"Local\PathDeck.Files.Show");
                ensureDesktop();
                if (!args.Contains("--background")) signal.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        return false;
    }
}
