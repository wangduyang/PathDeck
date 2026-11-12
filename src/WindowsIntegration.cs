using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuickFileDesk;

internal enum IntegrationState { Off, Enabled, NeedsUpdate }

internal sealed class WindowsIntegration(RegistryKey user, string executable, bool notifyShell = true)
{
    private const string Classes = @"Software\Classes\";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "1Commander";
    private const string Verb = "1Commander.Open";
    private const string PriorDefault = "PreviousDefault";
    private const string CreatedShell = "CreatedShellKey";
    private const string CreatedClass = "CreatedClassKey";
    private static readonly string[] FolderTypes = ["Directory", "Drive"];
    private static readonly Lazy<Task<Dispatcher>> ShortcutIconDispatcher = new(StartShortcutIconDispatcher);

    internal static string? OpenWith(Window owner, string path)
    {
        const uint execute = 0x4;
        const int cancelled = unchecked((int)0x800704C7);
        const int aborted = unchecked((int)0x80004004);
        var info = new OpenWithInfo { File = path, Flags = execute };
        var result = SHOpenWithDialog(new WindowInteropHelper(owner).Handle, in info);
        return result < 0 && result != cancelled && result != aborted
            ? $"无法显示打开方式（{Marshal.GetExceptionForHR(result)?.Message}）"
            : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenWithInfo
    {
        public string File;
        public string? Class;
        public uint Flags;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr owner, in OpenWithInfo info);

    private string Launcher => $"\"{executable}\"";

    // Appending a path segment keeps the closing quote from being escaped at a drive root.
    private string FolderCommand => $"{Launcher} \"%1\\.\"";

    public IntegrationState ReadDefault()
    {
        var owned = 0;
        var current = 0;
        foreach (var type in FolderTypes)
        {
            using var shell = user.OpenSubKey(ShellPath(type));
            if (shell is null) continue;
            using var verb = shell.OpenSubKey(Verb);
            var active = IsOurDefault(shell);
            if (active || verb is not null) owned++;
            if (!active || verb is null) continue;
            using var command = verb.OpenSubKey("command");
            if (IsCommand(command?.GetValue(null), FolderCommand)) current++;
        }
        return current == FolderTypes.Length ? IntegrationState.Enabled : owned == 0 ? IntegrationState.Off : IntegrationState.NeedsUpdate;
    }

    public IntegrationState ReadStartup()
    {
        using var run = user.OpenSubKey(StartupKey);
        var command = run?.GetValue(StartupName);
        return command is null ? IntegrationState.Off : IsCommand(command, Launcher) ? IntegrationState.Enabled : IntegrationState.NeedsUpdate;
    }

    public void SetDefault(bool enabled)
    {
        using var mutex = new Mutex(false, @"Local\1Commander.FolderAssociations");
        bool entered;
        try { entered = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new IOException("另一个 PathDeck 窗口正在修改文件夹关联，请稍后重试。");
        try { WriteDefault(enabled); }
        finally { mutex.ReleaseMutex(); }
    }

    private void WriteDefault(bool enabled)
    {
        if (!enabled)
        {
            try
            {
                foreach (var type in FolderTypes) RestoreDefault(type);
            }
            finally
            {
                NotifyAssociationsChanged();
            }
            return;
        }

        ValidateLauncher();
        var changed = new Stack<string>();
        try
        {
            foreach (var type in FolderTypes)
            {
                using var originalClass = user.OpenSubKey(Classes + type);
                using var originalShell = user.OpenSubKey(ShellPath(type));
                using var shell = user.CreateSubKey(ShellPath(type));
                var alreadyActive = IsOurDefault(shell);
                if (!alreadyActive) changed.Push(type);
                using var verb = shell.CreateSubKey(Verb);
                if (!alreadyActive)
                {
                    var previous = shell.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (previous is null) verb.DeleteValue(PriorDefault, throwOnMissingValue: false);
                    else verb.SetValue(PriorDefault, previous, shell.GetValueKind(""));
                }

                if (originalClass is null) verb.SetValue(CreatedClass, 1);
                if (originalShell is null) verb.SetValue(CreatedShell, 1);
                verb.SetValue("MUIVerb", "在 PathDeck 中打开");
                verb.SetValue("Icon", $"{Launcher},0");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", FolderCommand);
                shell.SetValue("", Verb);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            foreach (var type in changed)
            {
                try { RestoreDefault(type); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or SecurityException)
                {
                    throw new IOException("部分文件夹关联未能恢复，请在设置中再次点击恢复原设置。", new AggregateException(ex, restore));
                }
            }
            throw;
        }
        finally
        {
            NotifyAssociationsChanged();
        }
    }

    public void SetStartup(bool enabled)
    {
        if (!enabled)
        {
            using var existing = user.OpenSubKey(StartupKey, writable: true);
            existing?.DeleteValue(StartupName, throwOnMissingValue: false);
            return;
        }
        ValidateLauncher();
        if (Launcher.Length > 260) throw new IOException("程序路径过长，无法添加到 Windows 启动项。");
        using var run = user.CreateSubKey(StartupKey);
        run.SetValue(StartupName, Launcher, RegistryValueKind.String);
    }

    private void RestoreDefault(string type)
    {
        bool removeShell;
        bool removeClass;
        using (var shell = user.OpenSubKey(ShellPath(type), writable: true))
        {
            if (shell is null) return;
            using (var verb = shell.OpenSubKey(Verb))
            {
                removeShell = Equals(verb?.GetValue(CreatedShell), 1);
                removeClass = Equals(verb?.GetValue(CreatedClass), 1);
                if (IsOurDefault(shell))
                {
                    var previous = verb?.GetValue(PriorDefault, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (previous is null) shell.DeleteValue("", throwOnMissingValue: false);
                    else shell.SetValue("", previous, verb!.GetValueKind(PriorDefault));
                }
            }
            shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            removeShell &= shell.ValueCount == 0 && shell.SubKeyCount == 0;
        }
        if (!removeShell) return;
        user.DeleteSubKey(ShellPath(type), throwOnMissingSubKey: false);
        if (!removeClass) return;

        var classPath = Classes + type;
        using (var classKey = user.OpenSubKey(classPath))
        {
            if (classKey is null || classKey.ValueCount != 0 || classKey.SubKeyCount != 0) return;
        }
        user.DeleteSubKey(classPath, throwOnMissingSubKey: false);
    }

    private void ValidateLauncher()
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请从 PathDeck 的便携版可执行文件启动后再设置。");
    }

    private static string ShellPath(string type) => Classes + type + @"\shell";
    private static bool IsOurDefault(RegistryKey shell) => IsCommand(shell.GetValue(null), Verb);
    private static bool IsCommand(object? value, string expected) => value is string text && text.Equals(expected, StringComparison.OrdinalIgnoreCase);

    internal static async Task<BitmapSource?> ReadShortcutIconAsync(string path, CancellationToken token)
    {
        var dispatcher = await ShortcutIconDispatcher.Value.WaitAsync(token).ConfigureAwait(false);
        return await dispatcher.InvokeAsync(() => ReadShortcutIcon(path), DispatcherPriority.Background, token)
            .Task.ConfigureAwait(false);
    }

    private static Task<Dispatcher> StartShortcutIconDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true, Name = "Shortcut icons"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private static BitmapSource? ReadShortcutIcon(string path)
    {
        const uint shellIcon = 0x100;
        const uint linkOverlay = 0x8000;
        const uint apartmentThreaded = 0x2;
        if (CoInitializeEx(IntPtr.Zero, apartmentThreaded) < 0) return null;

        var info = new ShellFileInfo();
        try
        {
            if (SHGetFileInfoW(path, 0, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), shellIcon | linkOverlay) == IntPtr.Zero ||
                info.Icon == IntPtr.Zero)
                return null;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    internal static string? ReadShortcutFolder(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var buffer = new StringBuilder(260);
            if (((IShellLinkPath)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) != 0) return null;

            var target = buffer.ToString();
            return Path.IsPathFullyQualified(target) && Directory.Exists(target) ? target : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }

    internal static void SetWindowFrameTheme(Window window, bool darkMode)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        const int immersiveDarkMode = 20;
        const int captionColor = 35;
        const int textColor = 36;
        uint enabled = darkMode ? 1u : 0u;
        uint caption = darkMode ? 0x002C1C14u : uint.MaxValue;
        uint text = darkMode ? 0x00F6F0ECu : uint.MaxValue;
        DwmSetWindowAttribute(handle, immersiveDarkMode, in enabled, sizeof(uint));
        DwmSetWindowAttribute(handle, captionColor, in caption, sizeof(uint));
        DwmSetWindowAttribute(handle, textColor, in text, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in uint value, uint size);

    private void NotifyAssociationsChanged()
    {
        if (notifyShell) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

