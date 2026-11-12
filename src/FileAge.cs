using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuickFileDesk;

internal enum FileAgeBand { Recent, Day, Week, Month, Older }

internal sealed record FileAge(string Text, FileAgeBand Band)
{
    public static FileAge From(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return new("未来", FileAgeBand.Older);
        if (age.TotalMinutes < 1) return new("刚刚", FileAgeBand.Recent);
        if (age.TotalHours < 1) return new($"{(int)age.TotalMinutes} 分钟前", FileAgeBand.Recent);
        if (age.TotalDays < 1) return new($"{(int)age.TotalHours} 小时前", FileAgeBand.Day);

        var days = (int)age.TotalDays;
        if (days < 7) return new($"{days} 天前", FileAgeBand.Week);
        if (days < 30) return new($"{days} 天前", FileAgeBand.Month);
        if (days < 365) return new($"{Math.Min(days / 30, 11)} 个月前", FileAgeBand.Older);
        return new($"{days / 365} 年前", FileAgeBand.Older);
    }
}

public sealed class FileAgeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateTime updated, DateTime now]
            ? FileAge.From(now - updated.ToUniversalTime())
            : DependencyProperty.UnsetValue;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
