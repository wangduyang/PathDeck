using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TaskGlass;

internal static class ModuleNavigation
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "PathDeck.exe");
    internal static void OpenFiles(bool settings = false)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(settings ? @"Local\PathDeck.Files.Settings" : @"Local\PathDeck.Files.Show");
            signal.Set(); return;
        }
        catch (WaitHandleCannotBeOpenedException) { }
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (settings) start.ArgumentList.Add("--file-settings");
        Process.Start(start);
    }
}
