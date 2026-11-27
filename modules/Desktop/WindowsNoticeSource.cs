using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TaskGlass;

internal sealed class WindowsNoticeSource : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Action<Notice> publish;
    private readonly Action<string> status;
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.Ordinal);
    internal WindowsNoticeSource(Action<Notice> publish, Action<string> status)
    { this.publish = publish; this.status = status; _ = Task.Run(Run); }

    internal static string Category(string text)
    {
        if (text.Contains("微信") || text.Contains("Weixin", StringComparison.OrdinalIgnoreCase) || text.Contains("WeChat", StringComparison.OrdinalIgnoreCase)) return "微信";
        if (text.Contains("阿里邮箱") || text.Contains("AliMail", StringComparison.OrdinalIgnoreCase) || text.Contains("alibaba.alimail", StringComparison.OrdinalIgnoreCase)) return "阿里邮箱";
        if (new[] { "Chrome", "Edge", "Firefox", "浏览器" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))) return "浏览器";
        return string.IsNullOrWhiteSpace(text) ? "系统通知" : text.Trim();
    }

    private async Task Run()
    {
        var seen = new Dictionary<uint, string>();
        bool baseline = true;
        try
        {
            var listener = UserNotificationListener.Current;
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
                    {
                        status("系统通知尚未授权，请在 Windows 通知访问设置中允许 TaskGlass。");
                        seen.Clear(); baseline = true;
                    }
                    else
                    {
                        var all = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                        var current = new Dictionary<uint, string>();
                        foreach (var item in all)
                        {
                            if (stopping.IsCancellationRequested) return;
                            if (baseline) { current[item.Id] = ""; continue; }
                            try
                            {
                                // Older notifications remain an ID-only baseline; their message text is never loaded.
                                if (seen.TryGetValue(item.Id, out var prior) && (prior == "" || DateTimeOffset.Now - item.CreationTime > TimeSpan.FromMinutes(2)))
                                { current[item.Id] = prior; continue; }
                                string appId = item.AppInfo.AppUserModelId;
                                string app = item.AppInfo.DisplayInfo.DisplayName;
                                string category = Category(string.IsNullOrWhiteSpace(app) ? appId : app);
                                var binding = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)
                                    ?? item.Notification.Visual.Bindings.FirstOrDefault();
                                var texts = binding?.GetTextElements().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToArray() ?? Array.Empty<string>();
                                if (texts.Length == 0) texts = new[] { category, "收到一条新通知" };
                                string signature = string.Join("\n", texts);
                                current[item.Id] = signature;
                                if (prior == signature) continue;
                                string title = texts[0], body = string.Join("\n", texts.Skip(1));
                                publish(new Notice("windows:" + item.Id, category, title, body,
                                    () => { if (category == "微信") Native.OpenWeChat(); else OpenApp(appId); })
                                    { Icon = await ReadIcon(item.AppInfo, appId), SourceAppId = appId });
                            }
                            catch (Exception) { /* A toast can disappear during extraction. No content is logged. */ }
                        }
                        seen = current; baseline = false; status("系统通知已连接 · 全部应用（含飞书）· 显示来源图标");
                    }
                }
                catch (Exception error) { status($"系统通知暂不可用（0x{error.HResult:X8}）"); }
                await Task.Delay(1000, stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { status($"系统通知接口不可用（0x{error.HResult:X8}）"); }
    }

    private async Task<ImageSource?> ReadIcon(AppInfo app, string appId)
    {
        if (icons.TryGetValue(appId, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            using var stream = await app.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
            if (stream.Size > 0 && stream.Size <= 1024 * 1024)
            {
                using var reader = new DataReader(stream);
                uint length = await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[length]; reader.ReadBytes(bytes);
                using var memory = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64; bitmap.StreamSource = memory; bitmap.EndInit();
                bitmap.Freeze(); icon = bitmap;
            }
        }
        catch (Exception) { /* Missing app metadata or a removed icon must not discard the notification. */ }
        if (icon is null)
        {
            try { icon = await Task.Run(() => Native.ApplicationIcon(appId)); }
            catch (Exception) { /* Unregistered desktop apps can have no shell icon either. */ }
        }
        if (icons.Count >= 64) icons.Clear();
        icons[appId] = icon; return icon;
    }

    private static void OpenApp(string appId)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(@"shell:AppsFolder\" + appId); Process.Start(start);
    }
    public void Dispose() => stopping.Cancel();
}
