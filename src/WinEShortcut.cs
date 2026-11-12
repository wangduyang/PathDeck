using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace QuickFileDesk;

internal sealed class WinEShortcut : IDisposable
{
    private const int LowLevelKeyboard = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SysKeyDown = 0x0104;
    private const int SysKeyUp = 0x0105;
    private const int LetterE = 0x45;
    private const int LeftWin = 0x5B;
    private const int RightWin = 0x5C;
    private const int Control = 0x11;
    private const int Shift = 0x10;
    private const int Alt = 0x12;

    private readonly Thread thread;
    private readonly Dispatcher dispatcher;
    private bool consumed;

    public WinEShortcut(Action open)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() => Listen(ready, open)) { IsBackground = true, Name = "1Commander Win+E" };
        thread.Start();
        dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    private void Listen(TaskCompletionSource<Dispatcher> ready, Action open)
    {
        var messageLoop = Dispatcher.CurrentDispatcher;
        HookProc callback = (code, message, data) => HandleKey(code, message, data, open);
        var handle = SetWindowsHookExW(LowLevelKeyboard, callback, GetModuleHandleW(null), 0);
        if (handle == IntPtr.Zero)
        {
            ready.SetException(new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        ready.SetResult(messageLoop);
        try { Dispatcher.Run(); }
        finally
        {
            UnhookWindowsHookEx(handle);
            GC.KeepAlive(callback);
        }
    }

    private IntPtr HandleKey(int code, IntPtr message, IntPtr data, Action open)
    {
        if (code >= 0 && Marshal.ReadInt32(data) == LetterE)
        {
            var kind = message.ToInt32();
            if (kind is KeyUp or SysKeyUp && consumed)
            {
                consumed = false;
                return new IntPtr(1);
            }
            if (kind is KeyDown or SysKeyDown)
            {
                if (consumed) return new IntPtr(1);
                if ((Down(LeftWin) || Down(RightWin)) && !Down(Control) && !Down(Shift) && !Down(Alt))
                {
                    consumed = true;
                    SuppressStartMenu();
                    open();
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void SuppressStartMenu()
    {
        // Windows sees a non-modifier event so releasing Win does not also open Start.
        var input = new Input
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0xFF, Flags = 2 } }
        };
        SendInput(1, ref input, Marshal.SizeOf<Input>());
    }

    public void Dispose()
    {
        if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        thread.Join(1000);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, ref Input inputs, int size);
}

