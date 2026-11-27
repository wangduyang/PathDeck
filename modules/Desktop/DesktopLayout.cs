using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskGlass;

internal static class DesktopLayout
{
    internal sealed record Snapshot(nint Owner, nint View, Native.Rect Bounds, Native.Rect[] Icons);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint window, uint id, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible value);

    internal static Snapshot? Read()
    {
        try
        {
            var host = Native.DesktopView();
            if (host.View == 0 || !Native.GetWindowRect(host.View, out var bounds)) return null;
            var iid = typeof(Accessibility.IAccessible).GUID;
            if (AccessibleObjectFromWindow(host.View, 0xFFFFFFFC, in iid, out var value) < 0) return null;
            try
            {
                var icons = new List<Native.Rect>();
                for (int i = 1, count = value.accChildCount; i <= count; i++)
                {
                    try
                    {
                        value.accLocation(out int x, out int y, out int width, out int height, i);
                        if (width <= 0 || height <= 0 || x >= bounds.Right || y >= bounds.Bottom || x + width <= bounds.Left || y + height <= bounds.Top) continue;
                        icons.Add(new Native.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height });
                    }
                    catch (Exception) { /* A desktop item can disappear during enumeration. */ }
                }
                // Only geometry is queried: no accName, file paths, targets or file contents.
                return new Snapshot(host.Owner, host.View, bounds, icons.ToArray());
            }
            finally { Marshal.ReleaseComObject(value); }
        }
        catch (Exception) { return null; }
    }

    internal static Native.Rect[] Build(Native.Rect bounds, Native.Rect[] icons)
    {
        if (icons.Length == 0) return Array.Empty<Native.Rect>();
        var widths = icons.Select(r => r.Width).OrderBy(w => w).ToArray();
        double distance = Math.Max(24, widths[widths.Length / 2] * 1.25);
        var parents = Enumerable.Range(0, icons.Length).ToArray();
        int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        for (int i = 0; i < icons.Length; i++)
            for (int j = 0; j < i; j++)
            {
                double x = Math.Max(0, Math.Max(icons[i].Left - icons[j].Right, icons[j].Left - icons[i].Right));
                double y = Math.Max(0, Math.Max(icons[i].Top - icons[j].Bottom, icons[j].Top - icons[i].Bottom));
                if (x * x + y * y <= distance * distance) parents[Root(i)] = Root(j);
            }
        var groups = new Dictionary<int, Native.Rect>();
        static Native.Rect Union(Native.Rect a, Native.Rect b) => new()
        { Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top), Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom) };
        for (int i = 0; i < icons.Length; i++)
        { int root = Root(i); groups[root] = groups.TryGetValue(root, out var rect) ? Union(rect, icons[i]) : icons[i]; }
        int padding = Math.Max(10, (int)Math.Round(widths[widths.Length / 2] * .16));
        var regions = groups.Values.Select(rect => new Native.Rect
        { Left = Math.Max(bounds.Left, rect.Left - padding), Top = Math.Max(bounds.Top, rect.Top - padding), Right = Math.Min(bounds.Right, rect.Right + padding), Bottom = Math.Min(bounds.Bottom, rect.Bottom + padding) }).ToList();
        for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
                if (regions[i].Left <= regions[j].Right && regions[i].Right >= regions[j].Left && regions[i].Top <= regions[j].Bottom && regions[i].Bottom >= regions[j].Top)
                { regions[i] = Union(regions[i], regions[j]); regions.RemoveAt(j); i = -1; break; }
        return regions.OrderBy(r => r.Left).ThenBy(r => r.Top).ToArray();
    }
}
