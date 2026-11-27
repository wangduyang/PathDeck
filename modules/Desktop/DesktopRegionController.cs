using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskGlass;

internal sealed class DesktopRegionController : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer animation = new(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(16) };
    private readonly List<DesktopRegionWindow> windows = new();
    private DesktopLayout.Snapshot? snapshot;
    private bool reading, disposed;
    private int ticks;
    private Preferences appearance = new();
    private BackdropFrame? wallpaper;
    private bool wallpaperReading,wallpaperAgain;
    private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    internal void SetAppearance(Preferences value)
    {
        appearance = value;
        foreach (var window in windows) window.SetAppearance(value);
    }
    internal int Count => windows.Count;
    internal int BlurCount => windows.Count(window=>window.BackgroundLive);
    internal int FoldableCount => windows.Count(window=>window.CanFold);
    internal int CollapsedCount => windows.Count(window=>window.Collapsed);
    internal int IconCount => snapshot?.Icons.Length ?? 0;
    internal string Status { get; private set; } = "桌面分区：正在读取图标位置…";
    internal event Action? Changed;
    internal DesktopRegionController()
    {
        SystemEvents.UserPreferenceChanged+=PreferenceChanged;
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        ReadWallpaper();
        animation.Tick+=(_,_)=>Advance();
        timer.Tick += (_, _) =>
        {
            if (++ticks % 20 == 0) Refresh();
            if (snapshot is null || !Native.GetCursorPos(out var point)) return;
            nint hit = Native.WindowFromPoint(point);
            bool desktop=hit==snapshot.Owner||Native.IsChild(snapshot.Owner,hit)||windows.Any(window=>window.Owns(hit));
            bool busy=InteractionActive(snapshot.View);
            foreach (var window in windows) window.Pointer(point,desktop,busy);
        };
        timer.Start(); Refresh();
    }
    private async void Refresh()
    {
        if (reading || disposed) return;
        reading = true;
        try
        {
            var current = await Task.Run(DesktopLayout.Read);
            if (disposed) return;
            if (current is null)
            {
                Clear(); snapshot = null;
                SetStatus("桌面分区：等待可访问的桌面图标"); return;
            }
            if (snapshot is not null && current.Owner == snapshot.Owner && current.View == snapshot.View && current.Bounds.Equals(snapshot.Bounds) && current.Icons.SequenceEqual(snapshot.Icons)) return;
            var regions = DesktopLayout.Build(current.Bounds, current.Icons);
            if (snapshot?.Owner != current.Owner || snapshot?.View!=current.View || windows.Count != regions.Length) Clear();
            snapshot = current;
            while (windows.Count < regions.Length)
            {
                var window=new DesktopRegionWindow(current.Owner);window.DisplayChanged+=DisplayChanged;windows.Add(window);
            }
            for (int i = 0; i < regions.Length; i++) { windows[i].SetAppearance(appearance); windows[i].SetRegion(regions[i],current.Icons);if(wallpaper is not null)windows[i].SetWallpaper(wallpaper); windows[i].Place(); }
            SetStatus($"桌面分区已连接 · {current.Icons.Length} 个图标 · {regions.Length} 个区域");
        }
        catch (Exception error)
        {
            Clear(); snapshot = null;
            SetStatus($"桌面分区暂不可用（0x{error.HResult:X8}）");
        }
        finally { reading = false; }
    }
    private void SetStatus(string value)
    { Status = value; Changed?.Invoke(); }
    private void DisplayChanged(){Advance();Changed?.Invoke();}
    private void Advance()
    {
        long now=Environment.TickCount64;foreach(var window in windows)window.Advance(now);
        if(windows.Any(window=>window.Animating)){if(!animation.IsEnabled)animation.Start();}
        else animation.Stop();
    }
    private void Clear() { animation.Stop();foreach (var window in windows){window.DisplayChanged-=DisplayChanged;window.Close();} windows.Clear(); }
    private void PreferenceChanged(object sender,UserPreferenceChangedEventArgs e)
    {if(e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)dispatcher.BeginInvoke(ReadWallpaper);}
    private void DisplayChanged(object? sender,EventArgs e)=>dispatcher.BeginInvoke(ReadWallpaper);
    private async void ReadWallpaper()
    {
        if(disposed)return;
        if(wallpaperReading){wallpaperAgain=true;return;}
        wallpaperReading=true;
        try
        {
            var value=await Task.Run(DesktopWallpaper.Read);
            if(disposed)return;wallpaper=value;
            foreach(var window in windows){window.SetWallpaper(value);window.Place();}
            Changed?.Invoke();
        }
        catch(Exception error){if(!disposed)SetStatus($"桌面壁纸暂不可用（0x{error.HResult:X8}）");}
        finally{wallpaperReading=false;if(wallpaperAgain&&!disposed){wallpaperAgain=false;ReadWallpaper();}}
    }
    public void Dispose() { disposed = true;SystemEvents.UserPreferenceChanged-=PreferenceChanged;SystemEvents.DisplaySettingsChanged-=DisplayChanged; timer.Stop(); Clear(); snapshot = null;wallpaper=null; }

    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo
    {internal int Size;internal uint Flags;internal nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;internal Native.Rect CaretRect;}
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    private static bool InteractionActive(nint view)
    {
        if((GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0)return true;
        uint thread=Native.GetWindowThreadProcessId(view,out _);var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};
        return thread!=0&&GetGUIThreadInfo(thread,ref info)&&((info.Flags&0x1E)!=0||info.Capture!=0);
    }
}
