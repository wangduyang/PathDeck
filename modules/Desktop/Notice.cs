using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace TaskGlass;

internal sealed class Notice
{
    internal Notice(string key, string app, string title, string body, Action? open = null)
    { Key = key; App = app; Title = title.Length > 160 ? title[..160] : title; Body = body.Length > 2400 ? body[..2400] : body; Open = open; }
    internal string Key { get; }
    public string App { get; }
    public string Title { get; }
    public string Body { get; }
    public DateTimeOffset Time { get; } = DateTimeOffset.Now;
    public string TimeText => Time.LocalDateTime.ToString("HH:mm");
    public string Symbol => App switch { "微信" => "微", "阿里邮箱" => "邮", "浏览器" => "网", _ => "T" };
    public string Accent => App switch { "微信" => "#72D397", "阿里邮箱" => "#FFC574", "浏览器" => "#C2ABFF", _ => "#9BE0C4" };
    internal Action? Open { get; }
    internal ImageSource? Icon { get; init; }
    internal string? SourceAppId { get; init; }
}

internal sealed class NoticeStore
{
    internal ObservableCollection<Notice> Items { get; } = new();
    private readonly Dictionary<string, (string Source, DateTimeOffset Time)> recent = new(StringComparer.Ordinal);
    internal bool Add(Notice notice)
    {
        // A system toast and a client popup can describe the same message.
        string contentKey = notice.App + "\n" + notice.Title + "\n" + notice.Body;
        string source = notice.Key.Split(':')[0];
        if (recent.TryGetValue(contentKey, out var prior) && prior.Source != source && notice.Time - prior.Time < TimeSpan.FromSeconds(8)) return false;
        recent[contentKey] = (source, notice.Time);
        if (recent.Count > 400) recent.Clear();
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Key != notice.Key) continue;
            Items.RemoveAt(i); Items.Insert(0, notice); return true;
        }
        Items.Insert(0, notice);
        return true;
    }
    internal void Clear() { Items.Clear(); recent.Clear(); }
}
