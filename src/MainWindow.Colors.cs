using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuickFileDesk;

public partial class MainWindow
{
    private Dictionary<string, string> colors = new(StringComparer.OrdinalIgnoreCase);
    private MenuItem colorMenu = null!;

    private MenuItem CreateColorMenu()
    {
        var menu = new MenuItem { Header = "颜色标签", Icon = MenuGlyph("\uE8EC", "IconViolet") };
        foreach (var (name, color) in FileColors.Palette)
        {
            var choice = Menu(name, "", () => SetColor(color));
            choice.Icon = new Ellipse { Width = 10, Height = 10, Fill = (Brush)new BrushConverter().ConvertFromString(color)! };
            menu.Items.Add(choice);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("清除颜色标签", "", () => SetColor(null), "\uE75C", "IconSlate"));
        return menu;
    }

    private void SetColor(string? color)
    {
        var selected = Selection;
        if (selected.Length == 0 || working) return;
        if (!SaveColors(tags =>
        {
            foreach (var entry in selected)
            {
                if (color is null) tags.Remove(entry.FullPath);
                else tags[entry.FullPath] = color;
            }
        })) return;
        foreach (var entry in selected) entry.TagColor = color;
        Report(color is null ? $"已清除 {selected.Length} 项的颜色标签" : $"已设置 {selected.Length} 项的颜色标签");
    }

    private bool SaveColors(Action<Dictionary<string, string>> update)
    {
        try { colors = FileColors.Update(update); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"无法保存颜色标签（{ex.Message}）");
            return false;
        }
    }

    private bool MoveColors(IEnumerable<(string Source, string? Destination)> paths)
    {
        var changes = paths.ToArray();
        if (!changes.Any(change => colors.Keys.Any(path => FileColors.IsAtOrUnder(path, change.Source)))) return true;
        return SaveColors(tags =>
        {
            foreach (var (source, destination) in changes)
            {
                foreach (var entry in tags.Where(tag => FileColors.IsAtOrUnder(tag.Key, source)).ToArray())
                {
                    tags.Remove(entry.Key);
                    if (destination is not null)
                        tags[destination + entry.Key[source.Length..]] = entry.Value;
                }
            }
        });
    }
}

