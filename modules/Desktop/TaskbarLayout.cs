using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;

namespace TaskGlass;

internal static class TaskbarLayout
{
    internal static Native.Rect[] Read(nint window, Native.Rect bounds)
    {
        try
        {
            if (!Native.GetWindowRect(window, out var current) || !current.Equals(bounds)) return Array.Empty<Native.Rect>();
            var root = AutomationElement.FromHandle(window);
            var elements = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)));
            Rect apps = Rect.Empty, tray = Rect.Empty;
            for (int i = 0; i < elements.Count; i++)
            {
                var item = elements[i].Current;
                var rect = item.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                string id = item.AutomationId;
                // Read only control identity and geometry, never app/window titles or messages.
                if (id is "StartButton" or "TaskViewButton" or "SearchButton" or "SearchBox" || id.StartsWith("Appid:", StringComparison.Ordinal)) apps.Union(rect);
                else if (item.ClassName == "TrayNotifyWnd" || id == "SystemTrayIcon") tray.Union(rect);
            }
            if (apps.IsEmpty) return Array.Empty<Native.Rect>();
            Native.Rect Convert(Rect rect) => rect.IsEmpty ? new() : new()
            { Left = (int)Math.Floor(rect.Left), Top = (int)Math.Floor(rect.Top), Right = (int)Math.Ceiling(rect.Right), Bottom = (int)Math.Ceiling(rect.Bottom) };
            return Build(bounds, Convert(apps), Convert(tray), Math.Max(1, Native.GetDpiForWindow(window) / 96.0));
        }
        catch (Exception) { return Array.Empty<Native.Rect>(); }
    }

    internal static Native.Rect[] Build(Native.Rect bounds, Native.Rect apps, Native.Rect tray, double scale)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return Array.Empty<Native.Rect>();
        if (apps.Width <= 0) return new[] { bounds };
        int padding = (int)Math.Round(12 * scale);
        Native.Rect Capsule(Native.Rect rect) => new()
        { Left = Math.Max(bounds.Left, rect.Left - padding), Right = Math.Min(bounds.Right, rect.Right + padding), Top = bounds.Top, Bottom = bounds.Bottom };
        var main = Capsule(apps);
        if (main.Width <= 0) return new[] { bounds };
        int width = bounds.Width / 2, left = bounds.Left + (bounds.Width - width) / 2;
        main.Left = Math.Min(main.Left, left); main.Right = Math.Max(main.Right, left + width);
        if (tray.Width <= 0) return new[] { main };
        var system = Capsule(tray);
        if (system.Width <= 0) return new[] { bounds };
        if (main.Right + padding >= system.Left)
            return new[] { new Native.Rect { Left = Math.Min(main.Left, system.Left), Top = bounds.Top, Right = Math.Max(main.Right, system.Right), Bottom = bounds.Bottom } };
        return new[] { main, system };
    }

    internal static Native.Rect MirrorClock(Native.Rect bounds, Native.Rect apps, Native.Rect tray, double scale)
    {
        if (bounds.Width <= 0 || apps.Width <= 0 || tray.Width <= 0 || tray.Height <= 0 ||
            tray.Left < bounds.Left || tray.Right > bounds.Right || tray.Top < bounds.Top || tray.Bottom > bounds.Bottom) return new();
        int left = bounds.Left + bounds.Right - tray.Right;
        var clock = new Native.Rect { Left = left, Top = tray.Top, Right = left + tray.Width, Bottom = tray.Bottom };
        return clock.Right + (int)Math.Round(12 * scale) <= apps.Left ? clock : new();
    }

    internal static nint CreateRegion(Native.Rect bounds, Native.Rect[] sections, int radius)
    {
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception();
        try
        {
            foreach (var section in sections)
            {
                int inset = section.Width > 2 && section.Height > 2 ? 1 : 0;
                int r = Math.Max(0, Math.Min(radius, Math.Min(section.Width, section.Height) / 2) - inset);
                // The binary native clip sits inside the alpha-rendered rim, hiding its pixel staircase.
                nint part = Native.CreateRoundRectRgn(section.Left - bounds.Left + inset, section.Top - bounds.Top + inset,
                    section.Right - bounds.Left - inset, section.Bottom - bounds.Top - inset, r * 2, r * 2);
                if (part == 0) throw new Win32Exception();
                try { if (Native.CombineRgn(region, region, part, 2) == 0) throw new Win32Exception(); }
                finally { Native.DeleteObject(part); }
            }
            return region;
        }
        catch { Native.DeleteObject(region); throw; }
    }
}
