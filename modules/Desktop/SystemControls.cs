using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Forms = System.Windows.Forms;
using System.Windows.Automation;

namespace TaskGlass;

internal sealed record AudioState(bool Available, double Volume, bool Muted);
internal sealed record ResourceState(double? Cpu, uint Memory, double UsedMemoryGb, double? DiskBytes);
internal sealed record BrightnessState(bool Available, byte Value, string? Instance);

internal static class SystemControls
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,nuint extra);
    internal static void SwitchInputLanguage()
    { keybd_event(0x5B,0,0,0);keybd_event(0x20,0,0,0);keybd_event(0x20,0,2,0);keybd_event(0x5B,0,2,0); }
    internal static void OpenTray()
    {
        foreach(var bar in Native.Taskbars())
        {
            var root=AutomationElement.FromHandle(bar);
            var button=root.FindFirst(TreeScope.Descendants,new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty,"SystemTrayChevron"),new PropertyCondition(AutomationElement.AutomationIdProperty,"SystemTrayChevronButton")));
            if(button?.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern)==true){((InvokePattern)pattern).Invoke();return;}
        }
        keybd_event(0x5B,0,0,0);keybd_event(0x42,0,0,0);keybd_event(0x42,0,2,0);keybd_event(0x5B,0,2,0);
    }
    internal static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    internal static string NetworkDescription()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return "未连接网络";
            return (profile.IsWlanConnectionProfile ? "Wi-Fi" : "有线网络") + (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess ? " · 已联网" : " · 无互联网连接");
        }
        catch (Exception) { return "网络状态暂不可用"; }
    }
    internal static string BatteryDescription()
    {
        var status = Forms.SystemInformation.PowerStatus;
        if ((status.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) != 0) return "未检测到电池";
        string percent = status.BatteryLifePercent is >= 0 and <= 1 ? $"{status.BatteryLifePercent:P0}" : "电量不可用";
        string health = "容量与循环次数暂不可用";
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.DesignCapacityInMilliwattHours is > 0 && report.FullChargeCapacityInMilliwattHours is >= 0)
                health = $"电池健康 {Math.Round(100.0 * report.FullChargeCapacityInMilliwattHours.Value / report.DesignCapacityInMilliwattHours.Value):0}% · 循环次数暂不可用";
        }
        catch (Exception) { }
        return percent + (status.PowerLineStatus == Forms.PowerLineStatus.Online ? " · 接通电源" : " · 使用电池") + "\n" + health;
    }
    internal static async Task<Radio?> FindRadio(RadioKind kind)
    { try { return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind); } catch (Exception) { return null; } }
    internal static async Task<string> ToggleRadio(Radio radio)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return "系统不允许直接切换，请打开对应的系统设置。";
            var next = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
            if (await radio.SetStateAsync(next) != RadioAccessStatus.Allowed) return "系统拒绝了切换请求，请打开对应的系统设置。";
            await Task.Delay(350);
            return radio.State == next ? "" : "切换请求已发送，硬件尚未确认状态。";
        }
        catch (Exception) { return "此设备不支持直接切换，请打开对应的系统设置。"; }
    }
    private static object? ConnectWmi()
    {
        object? locator = null;
        try { locator = Activator.CreateInstance(System.Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!); return ((dynamic)locator!).ConnectServer(".", "root\\wmi"); }
        finally { Release(locator); }
    }
    internal static BrightnessState ReadBrightness(string? screenDevice = null)
    {
        object? services = null, items = null;
        try
        {
            services = ConnectWmi(); items = ((dynamic)services!).ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active=True");
            foreach (object item in (IEnumerable)items!)
                try
                {
                    string instance=(string)((dynamic)item).InstanceName;
                    // WMI addresses internal panels. Match the selected display's monitor ID before offering its slider.
                    if(screenDevice is not null && !MatchesMonitor(screenDevice,instance))continue;
                    return new BrightnessState(true, (byte)((dynamic)item).CurrentBrightness, instance);
                }
                finally { Release(item); }
        }
        catch (Exception) { }
        finally { Release(items); Release(services); }
        return new BrightnessState(false, 0, null);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct DisplayDevice
    { internal uint Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Description;internal uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Key; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool EnumDisplayDevices(string device,uint number,ref DisplayDevice display,uint flags);
    private static bool MatchesMonitor(string screenDevice,string instance)
    {
        for(uint i=0;i<16;i++)
        {
            var display=new DisplayDevice{Size=(uint)Marshal.SizeOf<DisplayDevice>()};
            if(!EnumDisplayDevices(screenDevice,i,ref display,0))break;
            if(!string.IsNullOrEmpty(display.Id) && instance.StartsWith(display.Id,StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
    internal static bool SetBrightness(BrightnessState state, double percent)
    {
        if (!state.Available || state.Instance is null) return false;
        object? services = null, items = null;
        try
        {
            services = ConnectWmi(); items = ((dynamic)services!).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=True");
            foreach (object item in (IEnumerable)items!)
                try
                {
                    if ((string)((dynamic)item).InstanceName != state.Instance) continue;
                    return Convert.ToUInt32(((dynamic)item).WmiSetBrightness(1u, (byte)Math.Clamp(percent, 0, 100))) == 0;
                }
                finally { Release(item); }
        }
        catch (Exception) { }
        finally { Release(items); Release(services); }
        return false;
    }
    internal static AudioState ReadAudio()
    {
        try { return WithAudio(volume => { Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float level)); Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted)); return new AudioState(true, level * 100, muted); }); }
        catch (Exception) { return new AudioState(false, 0, false); }
    }
    internal static bool SetVolume(double value)
    { try { return WithAudio(v => v.SetMasterVolumeLevelScalar((float)Math.Clamp(value / 100, 0, 1), 0) >= 0); } catch (Exception) { return false; } }
    internal static bool SetMute(bool value)
    { try { return WithAudio(v => v.SetMute(value, 0) >= 0); } catch (Exception) { return false; } }
    private static T WithAudio<T>(Func<IAudioEndpointVolume, T> action)
    {
        object? enumerator = null, endpoint = null, volume = null;
        try
        {
            enumerator = Activator.CreateInstance(System.Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out var device)); endpoint = device;
            var iid = typeof(IAudioEndpointVolume).GUID; Marshal.ThrowExceptionForHR(device.Activate(in iid, 23, 0, out volume));
            return action((IAudioEndpointVolume)volume);
        }
        finally { Release(volume); Release(endpoint); Release(enumerator); }
    }
    internal static void Release(object? item) { if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    { [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    { [PreserveSig] int Activate(in Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback); [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint count); [PreserveSig] int SetMasterVolumeLevel(float level, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context); [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level); [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context); [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level); [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

internal sealed class ResourceMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo
    { internal uint Size, Load; internal ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { internal uint Status; internal double Value; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryInfo value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    private ulong idle, kernel, user;
    private bool sampled;
    private nint query, counter;
    internal ResourceMonitor()
    {
        if (PdhOpenQuery(null, 0, out query) == 0 && PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", 0, out counter) == 0) PdhCollectQueryData(query);
    }
    internal ResourceState Read()
    {
        double? cpu = null, disk = null;
        if (GetSystemTimes(out var nextIdle, out var nextKernel, out var nextUser))
        {
            ulong total = nextKernel - kernel + nextUser - user, rest = nextIdle - idle;
            if (sampled && total > 0) cpu = Math.Clamp(100.0 * (total - Math.Min(total, rest)) / total, 0, 100);
            idle = nextIdle; kernel = nextKernel; user = nextUser; sampled = true;
        }
        var memory = new MemoryInfo { Size = (uint)Marshal.SizeOf<MemoryInfo>() };
        GlobalMemoryStatusEx(ref memory);
        if (query != 0 && counter != 0 && PdhCollectQueryData(query) == 0 && PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) == 0 && value.Status <= 1) disk = value.Value;
        return new ResourceState(cpu, memory.Load, (memory.Total - memory.Available) / 1073741824.0, disk);
    }
    public void Dispose() { if (query != 0) PdhCloseQuery(query); query = counter = 0; }
}
