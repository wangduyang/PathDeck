using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TaskGlass;

// WeChat versions without UIA rows still request the native taskbar flash.
internal sealed class WeChatSignalSource : Forms.NativeWindow, IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    private readonly uint shellMessage = RegisterWindowMessage("SHELLHOOK");
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Action<Notice> publish;
    private readonly Func<bool> enabled;
    private TimeSpan? lastFlash;
    internal bool Registered { get; }
    internal int Signals { get; private set; }
    internal int Posted { get; private set; }
    internal event Action? Changed;

    internal WeChatSignalSource(Action<Notice> publish, Func<bool> enabled)
    {
        this.publish = publish; this.enabled = enabled;
        CreateHandle(new Forms.CreateParams { Caption = "TaskGlass notification signals" });
        Registered = shellMessage != 0 && RegisterShellHookWindow(Handle);
    }

    internal bool AcceptFlash(TimeSpan elapsed)
    {
        bool separate = lastFlash is null || elapsed - lastFlash.Value >= TimeSpan.FromSeconds(2);
        lastFlash = elapsed;
        return elapsed >= TimeSpan.FromSeconds(3) && separate;
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == shellMessage && message.WParam == 0x8006)
        {
            try
            {
                Native.GetWindowThreadProcessId(message.LParam, out uint owner);
                using var process = Process.GetProcessById((int)owner);
                if (process.ProcessName is "Weixin" or "WeChat")
                {
                    Signals++;
                    bool fresh = AcceptFlash(clock.Elapsed);
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
                    if (fresh && enabled() && foreground != owner)
                    {
                        Posted++;
                        publish(new Notice("wechat:taskbar:" + Guid.NewGuid(), "微信", "微信有新消息", "",
                            Native.OpenWeChat));
                    }
                    Changed?.Invoke();
                }
            }
            catch (Exception) { /* The source window or process can disappear during delivery. */ }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Registered) DeregisterShellHookWindow(Handle);
        DestroyHandle();
    }
}
