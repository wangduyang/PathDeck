using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TaskGlass;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool EnumCallback(nint window, nint data);
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string type, string? name);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] internal static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] internal static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint destination, nint source, nint second, int mode);
    [DllImport("gdi32.dll")] internal static extern bool EqualRgn(nint first, nint second);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] internal static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("shell32.dll")] internal static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmExtendFrameIntoClientArea(nint window, in Margins margins);
    [StructLayout(LayoutKind.Sequential)] internal struct Margins { public int Left, Right, Top, Bottom; }

    internal static List<Rect> MonitorBounds()
    {
        var result = new List<Rect>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect bounds, nint data) =>
        {
            if (bounds.Width > 0 && bounds.Height > 0) result.Add(bounds);
            return true;
        }, 0);
        return result;
    }

    internal static Rect? WindowMonitorBounds(nint window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        // MonitorFromWindow uses restore bounds when the HWND is minimized.
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref info) ? info.Monitor : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ImageSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(ImageSize size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string name, nint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Text;
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint window, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("kernel32.dll")] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process, ref uint length, StringBuilder appId);

    internal static string? ApplicationId(nint window)
    {
        if (window == 0) return null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, in iid, out var store) >= 0)
            {
                var value = new PropertyValue();
                try
                {
                    var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                    if (store.GetValue(in key, out value) >= 0 && value.Type == 31 && value.Text != 0)
                    {
                        string? id = Marshal.PtrToStringUni(value.Text);
                        if (!string.IsNullOrWhiteSpace(id)) return id;
                    }
                }
                finally { PropVariantClear(ref value); Marshal.ReleaseComObject(store); }
            }
        }
        catch (Exception) { /* Windows can disappear while focus changes. */ }
        try
        {
            GetWindowThreadProcessId(window, out uint owner);
            using var process = OpenProcess(0x1000, false, owner); // QUERY_LIMITED_INFORMATION only.
            if (process.IsInvalid) return null;
            uint length = 512; var id = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
        }
        catch (Exception) { return null; }
    }

    internal static BitmapSource? ApplicationIcon(string appId)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, 0, in iid, out var factory) < 0) return null;
        nint bitmap = 0;
        try
        {
            // ICONONLY: resolve the registered app icon, never a document thumbnail.
            if (factory.GetImage(new ImageSize { Width = 64, Height = 64 }, 0x5, out bitmap) < 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    internal static List<(nint Window, uint ProcessId)> NotificationPopups(ISet<uint> processes)
    {
        var result = new List<(nint, uint)>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (!processes.Contains(owner) || !IsWindowVisible(window) || IsIconic(window)) return true;
            // Titled main/chat windows are explicitly excluded before accessibility is queried.
            if (GetWindowTextLength(window) > 0 || !GetWindowRect(window, out var rect)) return true;
            double scale = Math.Max(1, GetDpiForWindow(window) / 96.0);
            double width = rect.Width / scale, height = rect.Height / scale;
            if (width < 160 || width > 520 || height < 40 || height > 300) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            string type = name.ToString();
            if (type != "Chrome_WidgetWin_1" && !type.Contains("QWindow", StringComparison.Ordinal) &&
                !type.Contains("Notify", StringComparison.OrdinalIgnoreCase) && !type.Contains("Toast", StringComparison.OrdinalIgnoreCase)) return true;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) return true;
            double right = (monitor.Work.Right - rect.Right) / scale;
            double bottom = (monitor.Work.Bottom - rect.Bottom) / scale, top = (rect.Top - monitor.Work.Top) / scale;
            if (right < -3 || right > 48 || !((bottom >= -3 && bottom <= 96) || (top >= -3 && top <= 48))) return true;
            result.Add((window, owner)); return true;
        }, 0);
        return result;
    }

    internal static List<nint> WeChatWindows(ISet<uint> processes)
    {
        var result = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (!processes.Contains(owner)) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString().Contains("QWindow", StringComparison.Ordinal)) result.Add(window);
            return true;
        }, 0);
        return result;
    }

    internal static void OpenWeChat()
    {
        var owners = new HashSet<uint>();
        foreach (string name in new[] { "Weixin", "WeChat" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) owners.Add((uint)process.Id);
        if (owners.Count == 0) throw new InvalidOperationException("微信当前未运行，请从任务栏打开微信。");
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        if (owners.Contains(foreground)) return;
        // The user verified this global shortcut. WeChat restores its own Qt
        // visibility/input state; forcing the HWND visible leaves input inert.
        System.Windows.Forms.SendKeys.SendWait("^%w");
    }

    internal static void GlassFrame(nint window, bool noActivate)
    {
        int dark = 0, acrylic = 1, round = 1;
        DwmSetWindowAttribute(window, 20, in dark, 4);
        DwmSetWindowAttribute(window, 33, in round, 4);
        DwmSetWindowAttribute(window, 38, in acrylic, 4);
        var margins = new Margins();
        DwmExtendFrameIntoClientArea(window, in margins);
        if (noActivate) SetWindowLong(window, -20, (nint)(GetWindowLong(window, -20).ToInt64() | 0x08000000 | 0x80));
    }

    internal static (nint Owner, nint View) DesktopView()
    {
        (nint Owner, nint View) result = default;
        EnumWindows((window, _) =>
        {
            var type = new StringBuilder(128); GetClassName(window, type, type.Capacity);
            if (type.ToString() is not ("Progman" or "WorkerW")) return true;
            nint host = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            nint view = host == 0 ? 0 : FindWindowEx(host, 0, "SysListView32", null);
            if (view == 0 || !IsWindowVisible(view)) return true;
            result = (window, view); return false;
        }, 0);
        return result;
    }

    internal static List<nint> Taskbars()
    {
        var result = new List<nint>();
        // Start menu popups can omit the still-live taskbar from EnumWindows.
        foreach (string type in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            nint window = 0;
            while ((window = FindWindowEx(0, window, type, null)) != 0) result.Add(window);
        }
        return result;
    }

    internal static void CloseEngine(uint processId) => EnumWindows((window, _) =>
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner == processId) PostMessage(window, 0x0010, 0, 0);
        return true;
    }, 0);

    internal static bool CoveredByFullscreen(nint taskbar)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == taskbar) return false;
        var name = new StringBuilder(128); GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW") return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref info) || !GetWindowRect(foreground, out var rect)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }
}
