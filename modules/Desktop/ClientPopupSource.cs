using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TaskGlass;

internal sealed class ClientPopupSource : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Action<Notice> publish;
    private readonly Action<string> status;
    internal ClientPopupSource(Action<Notice> publish, Action<string> status)
    { this.publish = publish; this.status = status; _ = Task.Run(Run); }

    private async Task Run()
    {
        var owners = new Dictionary<uint, (string App, string Path)>();
        var seen = new Dictionary<nint, string>();
        var textOnly = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        int passes = 0; bool first = true;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (passes++ % 12 == 0)
                {
                    owners.Clear();
                    foreach (string processName in new[] { "Weixin", "WeChat", "AliMail" })
                        foreach (var process in Process.GetProcessesByName(processName))
                            using (process)
                                try
                                {
                                    string? app = WindowsNoticeSource.Category(processName);
                                    string? path = process.MainModule?.FileName;
                                    if (app is not null && path is not null) owners[(uint)process.Id] = (app, path);
                                }
                                catch (Exception) { }
                }
                var candidates = Native.NotificationPopups(owners.Keys.ToHashSet());
                var current = new Dictionary<nint, string>();
                foreach (var candidate in candidates)
                {
                    if (stopping.IsCancellationRequested) return;
                    try
                    {
                        // Only a small untitled popup in a screen corner reaches this code.
                        var root = AutomationElement.FromHandle(candidate.Window);
                        var elements = root.FindAll(TreeScope.Descendants, textOnly);
                        var texts = new List<string>();
                        for (int i = 0; i < elements.Count && i < 16; i++)
                        {
                            string value = elements[i].Current.Name.Trim();
                            if (value.Length > 0 && value is not "关闭" and not "Close") texts.Add(value);
                        }
                        if (texts.Count == 0) continue;
                        string signature = string.Join("\n", texts);
                        current[candidate.Window] = signature;
                        if (first || (seen.TryGetValue(candidate.Window, out var prior) && prior == signature)) continue;
                        var source = owners[candidate.ProcessId];
                        string path = source.Path;
                        publish(new Notice("popup:" + candidate.Window, source.App, texts[0], string.Join("\n", texts.Skip(1)),
                            () => { if (source.App == "微信") Native.OpenWeChat(); else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }));
                    }
                    catch (Exception) { /* A dismissed popup or inaccessible provider is skipped. */ }
                }
                seen = current; first = false;
                status("客户端提醒适配已启用 · 仅识别屏幕角落的新消息提示");
                await Task.Delay(500, stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { status($"客户端提醒适配暂不可用（0x{error.HResult:X8}）"); }
    }
    public void Dispose() => stopping.Cancel();
}
