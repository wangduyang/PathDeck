using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TaskGlass;

internal sealed class LauncherHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    private readonly HwndSource source;
    private readonly Action open;
    internal string Status { get; private set; } = "快捷键未注册";
    internal LauncherHotkey(Action open)
    {
        this.open = open;
        source = new HwndSource(new HwndSourceParameters("TaskGlass 快捷键") { ParentWindow = (nint)(-3), WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(Hook);
    }
    private nint Hook(nint window, int message, nint wp, nint lp, ref bool handled)
    { if (message == 0x312 && wp == 1) { handled = true; open(); } return 0; }
    internal static bool IsValid(string? text) => Parse(text, out _, out _);
    private static bool Parse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length) return false;
        foreach (string part in parts.Take(parts.Length - 1))
        { if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 2; else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 1; else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 4; else return false; }
        try { var parsed = (Key)new KeyConverter().ConvertFromString(parts[^1])!; key = (uint)KeyInterop.VirtualKeyFromKey(parsed); return key > 0 && parsed is not (Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift); }
        catch (NotSupportedException) { return false; }
        catch (FormatException) { return false; }
    }
    internal void Configure(string shortcut)
    {
        UnregisterHotKey(source.Handle, 1);
        if (!Parse(shortcut, out uint modifiers, out uint key)) { Status = "快捷键格式无效，请使用 Ctrl+Alt+Space 等组合"; return; }
        Status = RegisterHotKey(source.Handle, 1, modifiers | 0x4000, key) ? shortcut + " 已连接" : shortcut + " 被其他软件占用，请换一组快捷键";
    }
    internal void Disable() { UnregisterHotKey(source.Handle, 1); Status = "启动台已停用，快捷键已释放"; }
    public void Dispose() { UnregisterHotKey(source.Handle, 1); source.Dispose(); }
}
