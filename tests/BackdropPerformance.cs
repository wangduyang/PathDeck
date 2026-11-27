using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskGlass;
using QuickFileDesk;

internal static class BackdropPerformance
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window,uint affinity);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendMessage(nint window,uint message,nint wp,nint lp);
    internal static void Run()
    {
        var window=new Window {Title="PathDeck · 刷新耗时检查",Width=1260,Height=780,Content=new Border {Background=Brushes.DimGray},ShowInTaskbar=false};
        using var appearance=new SharedAppearance((QuickFileDesk.App)Application.Current);
        _=new WindowGlass(window,appearance);
        try
        {
            window.Show(); window.UpdateLayout();
            var handle=new WindowInteropHelper(window).Handle;
            var captures=new List<double>(); var affinities=new List<double>();
            for(int i=0;i<25;i++)
            {
                var timer=Stopwatch.StartNew(); GlassSnapshot.CaptureClient(window); captures.Add(timer.Elapsed.TotalMilliseconds);
                timer.Restart(); SetWindowDisplayAffinity(handle,0x11); SetWindowDisplayAffinity(handle,0); affinities.Add(timer.Elapsed.TotalMilliseconds);
            }
            Report("client snapshot",captures); Report("affinity exclusion pair",affinities);
            var dispatches=new List<double>();
            for(int i=0;i<25;i++)
            {
                var timer=Stopwatch.StartNew(); SendMessage(handle,0x0113,0x5044,0); dispatches.Add(timer.Elapsed.TotalMilliseconds);
            }
            Report("UI refresh dispatch",dispatches);
            if(dispatches.Order().ElementAt(23)>6) throw new Exception("Background refresh must not block the drag UI for a frame.");
        }
        finally {window.Close();}
    }
    private static void Report(string label,List<double> values)
    {
        values.Sort(); Console.WriteLine($"{label}: p50={values[values.Count/2]:F2}ms p95={values[(int)(values.Count*.95)]:F2}ms max={values[^1]:F2}ms");
    }
}
