using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TaskGlass;

internal sealed class WeChatUnreadSource : IDisposable
{
    internal sealed record Row(string Chat, string Summary, int Unread, bool Muted);
    internal static Row Parse(string value)
    {
        var lines = value.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (lines.Length == 0) return new("", "", 0, false);
        string summary = ""; int unread = 0; bool muted = false;
        foreach (string original in lines.Skip(1))
        {
            string line = original;
            if (line == "消息免打扰") { muted = true; continue; }
            var count = Regex.Match(line, @"^\[(\d+)条\]\s*(.*)$");
            if (count.Success)
            { int.TryParse(count.Groups[1].Value, out unread); line = count.Groups[2].Value.Trim(); }
            // An unread-prefixed preview is message content, even if it matches the pin label.
            if (!count.Success && line == "已置顶") continue;
            bool time = line == "昨天" || line.StartsWith("星期", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\d{1,4}([:/.-]\d{1,2}){1,2}$");
            if (summary.Length == 0 && line.Length > 0 && !time) summary = line;
        }
        return new(lines[0], summary, unread, muted);
    }

    private readonly CancellationTokenSource stopping = new();
    private readonly Action<Notice> publish;
    private readonly Action<string> markRead;
    private readonly Action<string> status;
    internal volatile bool Available;
    internal int CachedRows { get; private set; }
    internal WeChatUnreadSource(Action<Notice> publish, Action<string> markRead, Action<string> status)
    { this.publish = publish; this.markRead = markRead; this.status = status; _ = Task.Run(Run); }

    private async Task Run()
    {
        var unread = new Dictionary<string, int>();
        var processes = new HashSet<uint>();
        var held = new List<AutomationElement>();
        var condition = new PropertyCondition(AutomationElement.ClassNameProperty, "mmui::ChatSessionCell");
        bool baseline = true; int passes = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (passes++ % 5 == 0)
                {
                    processes.Clear();
                    foreach (string name in new[] { "Weixin", "WeChat" })
                        foreach (var process in Process.GetProcessesByName(name))
                            using (process)
                                try { processes.Add((uint)process.Id); }
                                catch (Exception) { }
                }
                if (processes.Count == 0)
                { unread.Clear(); held.Clear(); CachedRows = 0; baseline = true; Available = false; status("微信未读摘要：等待微信启动"); }
                else
                {
                    // Keep live element references while the window is available, without reading message text in the foreground.
                    var elements = new List<AutomationElement>();
                    foreach (var window in Native.WeChatWindows(processes))
                    {
                        try
                        {
                            var root = AutomationElement.FromHandle(window);
                            var found = root.FindAll(TreeScope.Descendants, condition);
                            for (int i = 0; i < found.Count && i < 100; i++) elements.Add(found[i]);
                            if (elements.Count > 0) break;
                        }
                        catch (Exception) { }
                    }
                    if (elements.Count > 0) held = elements;
                    else elements = held;
                    CachedRows = held.Count;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
                    if (processes.Contains(foreground))
                    { baseline = true; status("微信未读摘要：微信在前台，暂停采集"); }
                    else
                    {
                        int read = 0;
                        foreach (var element in elements.ToArray())
                        {
                            if (stopping.IsCancellationRequested) return;
                            try
                            {
                                Row row = Parse(element.Current.Name);
                                if (row.Chat.Length == 0) continue;
                                string key = element.Current.AutomationId;
                                if (key.Length == 0) key = row.Chat;
                                string noticeKey = "wechat:" + key;
                                bool existed = unread.TryGetValue(key, out int previous);
                                bool increased = !baseline && !row.Muted && (existed ? row.Unread > previous : row.Unread > 0 && read < 3);
                                if (increased)
                                {
                                    publish(new Notice(noticeKey, "微信", row.Chat, row.Summary,
                                        Native.OpenWeChat));
                                }
                                if (existed && previous > 0 && row.Unread == 0) markRead(noticeKey);
                                unread[key] = row.Unread; read++;
                            }
                            catch (Exception) { held.Remove(element); }
                        }
                        CachedRows = held.Count;
                        Available = read > 0;
                        if (Available) { baseline = false; status("微信未读摘要：已连接，仅跟踪新增未读"); }
                        else { baseline = true; status("微信未读摘要：暂未连接，请打开微信窗口一次后切回其他软件"); }
                        if (unread.Count > 512) { unread.Clear(); baseline = true; }
                    }
                }
                await Task.Delay(2000, stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Available = false; status($"微信未读摘要暂不可用（0x{error.HResult:X8}）"); }
    }
    public void Dispose() => stopping.Cancel();
}
