using System.Diagnostics;

namespace QuickFileDesk;

internal static class DesktopModule
{
    internal static void EnsureStarted()
    {
        try { using var existing = Mutex.OpenExisting(@"Local\TaskGlass.Instance"); }
        catch (WaitHandleCannotBeOpenedException) { Start(); }
    }

    private static void Start()
    {
        var start = new ProcessStartInfo(TaskGlass.ModuleNavigation.Executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--desktop-module");
        start.ArgumentList.Add("--background");
        Process.Start(start);
    }

}
