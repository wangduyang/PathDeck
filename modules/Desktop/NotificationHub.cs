using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace TaskGlass;

internal sealed class NotificationHub : IDisposable
{
    internal NoticeStore Store { get; } = new();
    internal Dictionary<string, int> Received { get; } = new();
    private ToastStack? cards;
    private double blur = 60;
    private double materialOpacity = 45;
    private string color = GlassTheme.NotificationTint;
    private readonly DispatcherTimer activity = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Func<nint> getForegroundWindow;
    private readonly Func<nint, (string? AppId, string? App)> identifyForeground;
    private nint foreground;
    private bool displayAllowed = true;
    private Rect workArea = SystemParameters.WorkArea;
    internal NotificationHub() : this(Native.GetForegroundWindow, ReadForegroundSource) { }
    internal NotificationHub(Func<nint> getForegroundWindow, Func<nint, (string? AppId, string? App)> identifyForeground)
    {
        this.getForegroundWindow = getForegroundWindow;
        this.identifyForeground = identifyForeground;
        foreground = getForegroundWindow();
        activity.Tick += (_, _) =>
        {
            if (Store.Items.Count > 0)
            {
                UpdateDisplay();
                var area = SystemParameters.WorkArea;
                if (area != workArea) { workArea = area; if (displayAllowed) cards?.Refresh(); }
            }
            nint window = getForegroundWindow();
            if (window == foreground) return;
            foreground = window;
            DismissForegroundSource(window);
        };
        activity.Start();
    }
    private static (string? AppId, string? App) ReadForegroundSource(nint window)
    {
        string? appId = Native.ApplicationId(window);
        Native.GetWindowThreadProcessId(window, out uint owner);
        try
        {
            using var process = Process.GetProcessById((int)owner);
            return (appId, process.ProcessName switch { "Weixin" or "WeChat" => "微信", "AliMail" => "阿里邮箱", _ => null });
        }
        catch (Exception) { return (appId, null); }
    }
    private void DismissForegroundSource(nint window)
    {
        var source = identifyForeground(window);
        if (!string.IsNullOrWhiteSpace(source.AppId)) DismissSource(source.AppId);
        if (!string.IsNullOrWhiteSpace(source.App)) DismissApp(source.App);
    }
    internal event Action? Changed;
    internal bool Paused { get; private set; }
    internal void SetPaused(bool value) { Paused = value; UpdateDisplay(); Changed?.Invoke(); }
    internal void Reveal() { UpdateDisplay(); if (displayAllowed) cards?.Refresh(); }
    internal string SystemStatus { get; private set; } = "正在连接系统通知…";
    internal string ClientStatus { get; private set; } = "等待客户端新消息提示…";
    internal string WeChatStatus { get; private set; } = "微信未读摘要未启用";
    internal DateTimeOffset? LastPopupAt { get; private set; }
    internal void SetBlur(double value)
    {
        blur = double.IsNaN(value) ? 60 : Math.Clamp(value, 0, 100);
        cards?.SetBlur(blur);
    }
    internal void SetColor(string value) { color = value; cards?.SetColor(value); }
    internal void SetMaterialOpacity(double value) { materialOpacity = value; cards?.SetMaterialOpacity(value); }
    internal void SetWeChatStatus(string value)
    { if (WeChatStatus == value) return; WeChatStatus = value; Changed?.Invoke(); }
    internal void SetStatus(bool system, string value)
    {
        if (system ? SystemStatus == value : ClientStatus == value) return;
        if (system) SystemStatus = value; else ClientStatus = value;
        Changed?.Invoke();
    }
    internal void Post(Notice notice)
    {
        if (!Store.Add(notice)) return;
        Received[notice.App] = Received.GetValueOrDefault(notice.App) + 1;
        // Delivery can be queued before activation and reach us after its HWND already became current.
        DismissForegroundSource(getForegroundWindow());
        if (!Store.Items.Contains(notice)) return;
        UpdateDisplay();
        if (!displayAllowed) { Changed?.Invoke(); return; }
        cards ??= new ToastStack(this); cards.SetColor(color); cards.SetBlur(blur); cards.SetMaterialOpacity(materialOpacity); cards.Refresh();
        LastPopupAt = DateTimeOffset.Now; Changed?.Invoke();
    }
    private void UpdateDisplay()
    {
        if (Paused) { SetDisplayAllowed(false); return; }
        Native.SHQueryUserNotificationState(out int state);
        bool fullscreen = Native.Taskbars().Any(Native.CoveredByFullscreen);
        SetDisplayAllowed(!Paused && !fullscreen && state is not (3 or 4 or 6));
    }
    internal void SetDisplayAllowed(bool value)
    {
        if (displayAllowed == value) return;
        displayAllowed = value;
        if (!value) cards?.Hide();
        else if (Store.Items.Count > 0)
        {
            cards ??= new ToastStack(this); cards.SetColor(color); cards.SetBlur(blur); cards.SetMaterialOpacity(materialOpacity); cards.Refresh();
            LastPopupAt = DateTimeOffset.Now;
        }
        Changed?.Invoke();
    }
    internal void Remove(Notice notice)
    { Store.Items.Remove(notice); if (displayAllowed) cards?.Refresh(); Changed?.Invoke(); }
    internal void MarkRead(string key)
    {
        var notice = Store.Items.FirstOrDefault(item => item.Key == key);
        if (notice is not null) Remove(notice);
    }
    internal void DismissApp(string app)
    { foreach (var notice in Store.Items.Where(item => item.App == app).ToArray()) Remove(notice); }
    internal void DismissSource(string appId)
    { foreach (var notice in Store.Items.Where(item => string.Equals(item.SourceAppId, appId, StringComparison.Ordinal)).ToArray()) Remove(notice); }
    internal void Open(Notice notice)
    {
        Store.Items.Remove(notice); cards?.Close(notice.Key);
        try { notice.Open?.Invoke(); }
        catch (Exception) { System.Windows.MessageBox.Show("来源应用暂时无法打开，请从任务栏进入。", "PathDeck"); }
        finally { if (displayAllowed) cards?.Refresh(); Changed?.Invoke(); }
    }
    internal void Test() => Post(new Notice("test:" + Guid.NewGuid(), "PathDeck", "测试通知", "这是一条测试消息。"));
    public void Dispose() { activity.Stop(); cards?.Dispose(); Store.Clear(); }
}
