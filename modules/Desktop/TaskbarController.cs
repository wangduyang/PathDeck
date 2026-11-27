using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal sealed class TaskbarController : IDisposable
{
    private sealed class Bar
    {
        internal nint OriginalRegion;
        internal Native.Rect Bounds;
        internal double Radius = -1;
        internal Native.Rect[] Sections = Array.Empty<Native.Rect>();
        internal readonly GlowWindow[] Glows = { new(), new() };
        internal bool Reading;
        internal long NextRead;
    }
    private readonly Dictionary<nint, Bar> bars = new();
    private readonly TaskbarClockWindow clock = new();
    private Native.Rect clockBounds;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Preferences preferences;
    private int ticks;
    internal int Count => bars.Count;
    internal int SectionCount => bars.Values.Sum(bar => bar.Sections.Length);
    internal bool ClockVisible => clock.Live;
    internal string ClockText => clock.TimeText;
    internal Native.Rect ClockBounds => clockBounds;
    internal event Action? LayoutChanged;

    internal TaskbarController(Preferences preferences)
    {
        this.preferences = preferences;
        timer.Tick += (_, _) => Tick();
        Refresh(); timer.Start();
    }
    internal void Update(Preferences settings) { preferences = settings; Refresh(force: true); }

    private void Tick()
    {
        if (++ticks % 8 == 0) Refresh();
        UpdateClock();
        if (!Native.GetCursorPos(out var cursor)) return;
        foreach (var bar in bars.Values)
        {
            for (int i = 0; i < bar.Sections.Length; i++)
            {
                var rect = bar.Sections[i];
                bool hover = cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
                bar.Glows[i].SetHover(hover);
                if (hover) bar.Glows[i].MoveLight((double)(cursor.X - rect.Left) / Math.Max(1, rect.Width));
            }
        }
    }

    private void Refresh(bool force = false)
    {
        var current = Native.Taskbars();
        foreach (var window in bars.Keys.Where(window => !current.Contains(window)).ToArray()) Remove(window);
        foreach (var window in current)
        {
            if (!Native.GetWindowRect(window, out var rect) || rect.Width <= 0 || rect.Height <= 0) continue;
            if (!bars.TryGetValue(window, out var bar))
            {
                bar = new Bar();
                nint original = Native.CreateRectRgn(0, 0, 0, 0);
                if (Native.GetWindowRgn(window, original) == 0) Native.DeleteObject(original);
                else bar.OriginalRegion = original;
                bars.Add(window, bar);
            }
            double scale = Native.GetDpiForWindow(window) / 96.0;
            // Keep the saved 48-DIP design proportions when the native taskbar is enlarged.
            double heightScale = rect.Height / (48 * scale);
            int radius = (int)Math.Min(preferences.Radius * scale * heightScale, rect.Height / 2.0);
            bool moved = !rect.Equals(bar.Bounds);
            bool changed = force || moved || bar.Radius != radius;
            if (moved)
            {
                bar.Bounds = rect; bar.Sections = new[] { rect }; bar.NextRead = 0;
            }
            bar.Radius = radius;
            ApplyRegion(window, bar); PlaceGlows(window, bar, changed);
            if (!bar.Reading && Environment.TickCount64 >= bar.NextRead) ReadLayout(window, bar);
        }
        UpdateClock();
    }

    private void UpdateClock()
    {
        var primary = bars.FirstOrDefault(pair => Forms.Screen.FromHandle(pair.Key).Primary);
        Native.Rect next = new();
        if (primary.Value is not null && primary.Value.Sections.Length == 2 &&
            Native.IsWindowVisible(primary.Key) && !Native.CoveredByFullscreen(primary.Key))
            next = TaskbarLayout.MirrorClock(primary.Value.Bounds, primary.Value.Sections[0], primary.Value.Sections[1], Math.Max(1, Native.GetDpiForWindow(primary.Key) / 96.0));
        if (next.Width <= 0)
        {
            if (clock.Live || clockBounds.Width > 0)
            { clock.HideClock(); clockBounds = new(); LayoutChanged?.Invoke(); }
            return;
        }
        bool changed = !clock.Live || !clockBounds.Equals(next);
        clock.Place(next, preferences); clockBounds = next;
        if (changed) LayoutChanged?.Invoke();
    }

    private async void ReadLayout(nint window, Bar bar)
    {
        bar.Reading = true; bar.NextRead = Environment.TickCount64 + 1000;
        var bounds = bar.Bounds;
        try
        {
            var sections = await Task.Run(() => TaskbarLayout.Read(window, bounds));
            if (!bars.TryGetValue(window, out var current) || !ReferenceEquals(bar, current) || !bar.Bounds.Equals(bounds)) return;
            // Shell popups may temporarily make the button tree unavailable.
            if (sections.Length == 0) return;
            if (bar.Sections.SequenceEqual(sections)) return;
            bar.Sections = sections;
            ApplyRegion(window, bar); PlaceGlows(window, bar, true); UpdateClock(); LayoutChanged?.Invoke();
        }
        finally { bar.Reading = false; }
    }

    private static void ApplyRegion(nint window, Bar bar)
    {
        nint region = TaskbarLayout.CreateRegion(bar.Bounds, bar.Sections, (int)bar.Radius);
        nint current = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (Native.GetWindowRgn(window, current) != 0 && Native.EqualRgn(region, current)) return;
            if (Native.SetWindowRgn(window, region, true) == 0) throw new Win32Exception();
            region = 0; // Windows owns the successfully assigned region.
        }
        finally { if (region != 0) Native.DeleteObject(region); if (current != 0) Native.DeleteObject(current); }
    }

    private void PlaceGlows(nint window, Bar bar, bool moved)
    {
        bool show = Native.IsWindowVisible(window) && bar.Bounds.Height > 12 && !Native.CoveredByFullscreen(window);
        for (int i = 0; i < bar.Glows.Length; i++)
        {
            var glow = bar.Glows[i];
            if (!show || i >= bar.Sections.Length) { if (glow.IsVisible) glow.Hide(); }
            else if (moved || !glow.IsVisible) { glow.SetColor(preferences.TaskbarColor); glow.Place(bar.Sections[i], bar.Radius / Math.Max(1, Native.GetDpiForWindow(window) / 96.0)); }
        }
    }

    internal bool RoundedRegionsPresent()
    {
        if (bars.Count == 0) return false;
        foreach (var pair in bars)
        {
            nint region = Native.CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (Native.GetWindowRgn(pair.Key, region) == 0) return false;
                foreach (var section in pair.Value.Sections)
                {
                    int x = section.Left - pair.Value.Bounds.Left, y = section.Top - pair.Value.Bounds.Top;
                    if (!Native.PtInRegion(region, x + section.Width / 2, y + section.Height / 2)) return false;
                    if (preferences.Radius > 2 && Native.PtInRegion(region, x, y)) return false;
                }
            }
            finally { Native.DeleteObject(region); }
        }
        return true;
    }

    private void Remove(nint window)
    {
        var bar = bars[window]; bars.Remove(window);
        foreach (var glow in bar.Glows) glow.Close();
        if (Native.IsWindow(window))
        {
            nint restore = 0;
            if (bar.OriginalRegion != 0)
            { restore = Native.CreateRectRgn(0, 0, 0, 0); Native.CombineRgn(restore, bar.OriginalRegion, 0, 5); }
            if (Native.SetWindowRgn(window, restore, true) == 0 && restore != 0) Native.DeleteObject(restore);
        }
        if (bar.OriginalRegion != 0) Native.DeleteObject(bar.OriginalRegion);
    }

    public void Dispose()
    { timer.Stop(); clock.Close(); foreach (var window in bars.Keys.ToArray()) Remove(window); }
}
