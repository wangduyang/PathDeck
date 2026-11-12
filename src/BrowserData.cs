using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace QuickFileDesk;

internal sealed class FileEntry(string fullPath, string name, bool isDirectory, bool hidden, long length, DateTime updated)
    : INotifyPropertyChanged
{
    public string FullPath { get; } = fullPath;
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public bool Hidden { get; } = hidden;
    public long Length { get; } = length;
    public DateTime Updated { get; } = updated;

    private Color? iconColor;
    private string? tagColor;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string? TagColor
    {
        get => tagColor;
        set
        {
            if (tagColor == value) return;
            tagColor = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagColor)));
        }
    }

    public Color IconColor => iconColor ??= ColorForExtension(ArchiveFiles.GetExtension(Name) ?? Path.GetExtension(Name).ToLowerInvariant());

    private static Color ColorForExtension(string extension)
    {
        var rgb = extension switch
        {
            "" => 0x6E7F97,
            ".ppt" => 0xB86450,
            ".pptx" => 0xAE5844,
            ".zip" => 0xA3762B,
            ".rar" => 0xA06439,
            ".7z" => 0x8A7350,
            ".exe" => 0x7161A7,
            ".msi" => 0x866094,
            ".apk" => 0x4F8755,
            ".whl" => 0x497E86,
            ".json" => 0x4E8375,
            ".pdf" => 0xAE535B,
            ".docx" => 0x4E77AB,
            ".xlsx" => 0x427D6A,
            ".txt" => 0x687A92,
            ".png" => 0x4F91B6,
            ".jpg" => 0x5676AB,
            ".jpeg" => 0x586F9A,
            ".bmp" => 0x537D8B,
            ".gif" => 0x7867A4,
            ".tif" => 0x387C84,
            ".tiff" => 0x486F90,
            ".ico" => 0x6D72A4,
            _ => 0
        };
        if (rgb != 0)
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        var hash = 2166136261u;
        foreach (var character in extension)
            hash = unchecked((hash ^ character) * 16777619u);

        // Keep uncommon suffixes in the same muted range as the fixed colors.
        var hue = (hash % 3600) / 600d;
        var sector = (int)hue;
        var low = (byte)(74 + (hash >> 12) % 22);
        var high = (byte)(144 + (hash >> 18) % 24);
        var rising = (byte)Math.Round(low + (high - low) * (hue - sector));
        var falling = (byte)(low + high - rising);
        return sector switch
        {
            0 => Color.FromRgb(high, rising, low),
            1 => Color.FromRgb(falling, high, low),
            2 => Color.FromRgb(low, high, rising),
            3 => Color.FromRgb(low, falling, high),
            4 => Color.FromRgb(rising, low, high),
            _ => Color.FromRgb(high, low, falling)
        };
    }

    public string Type => IsDirectory ? "文件夹" : NiftiFiles.IsNifti(Name) ? "NIfTI 文件" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext
        ? $"{ext} 文件" : "文件";
    public string Size => IsDirectory ? "" : BrowserData.FormatSize(Length);
    public string UpdatedText => Updated.ToString("yyyy-MM-dd HH:mm");
    public bool IsShortcut => !IsDirectory && Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    public bool IsImage => !IsDirectory && Path.GetExtension(Name).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico";
}

internal sealed class BrowserTab(string path)
{
    public const string DriveOverviewPath = "";
    public string Path { get; set; } = path;
    public bool IsDriveOverview => Path == DriveOverviewPath;
    public List<string> Back { get; } = [];
    public List<string> Forward { get; } = [];
    public FileEntry[] Entries { get; set; } = [];
    public bool Loaded { get; set; }
}

internal sealed record Location(string Label, string Path, string Icon = "\uE8B7");

internal sealed class DriveEntry(DriveInfo drive) : INotifyPropertyChanged
{
    private const long Terabyte = 1024L * 1024 * 1024 * 1024;
    private bool loading;
    private (string Text, double? UsedPercent) capacity = ("正在读取容量…", null);

    public string Path => drive.Name;
    public string Label => Path.TrimEnd('\\');
    public string CapacityText => capacity.Text;
    public double UsedPercent => capacity.UsedPercent ?? 0;
    public string UsageText => capacity.UsedPercent is double percent ? $"{percent:0}%" : "—";
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadCapacity()
    {
        if (loading) return;
        loading = true;
        try
        {
            capacity = await Task.Run(ReadCapacity);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        finally
        {
            loading = false;
        }
    }

    private (string Text, double? UsedPercent) ReadCapacity()
    {
        try
        {
            var total = drive.TotalSize;
            var available = drive.AvailableFreeSpace;
            var text = $"总容量 {FormatCapacity(total)}\n剩余容量 {FormatCapacity(available)}";
            double? percent = total > 0 ? Math.Clamp((1 - (double)available / total) * 100, 0, 100) : null;
            return (text, percent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("无法读取容量", null);
        }
    }

    private static string FormatCapacity(long bytes) => bytes >= Terabyte
        ? $"{bytes / (double)Terabyte:N2} TB" : BrowserData.FormatSize(bytes);
}

internal static class BrowserData
{
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB"
    };

    public static FileEntry[] List(string path, string? search, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = search is not null,
            RecurseSubdirectories = search is not null,
            AttributesToSkip = search is null ? 0 : FileAttributes.ReparsePoint
        };
        var entries = new List<FileEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            if (search is not null && !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (ArchiveFiles.IsStagingPath(item.FullName)) continue;
            try
            {
                var flags = item.Attributes;
                var isFolder = (flags & FileAttributes.Directory) != 0;
                var hidden = (flags & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                var length = item is FileInfo file ? file.Length : 0;
                entries.Add(new(item.FullName, item.Name, isFolder, hidden, length, item.LastWriteTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The directory may change while its entries are being enumerated.
            }
        }

        return entries.OrderBy(item => !item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal static class PinnedLocations
{
    public static List<Location> Load() => PortableSettings.Load<List<Location>>("pins.json") ?? [];

    public static List<Location> Update(Action<List<Location>> change) =>
        PortableSettings.Update("pins.json", @"Local\QuickFileDesk.Pins", Load, change);
}

internal static class FileColors
{
    public static readonly (string Name, string Hex)[] Palette =
    [
        ("红色", "#FF453A"), ("橙色", "#FF9500"), ("黄色", "#FFCC00"),
        ("绿色", "#34C759"), ("蓝色", "#007AFF"), ("紫色", "#AF52DE"), ("灰色", "#8E8E93")
    ];

    public static Dictionary<string, string> Load()
    {
        var saved = PortableSettings.Load<Dictionary<string, string>>("colors.json");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (saved is not null)
            foreach (var (path, color) in saved)
                if (Palette.Any(choice => choice.Hex == color)) result[path] = color;
        return result;
    }

    public static Dictionary<string, string> Update(Action<Dictionary<string, string>> change) =>
        PortableSettings.Update("colors.json", @"Local\QuickFileDesk.Colors", Load, change);

    public static bool IsAtOrUnder(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > parent.Length && path[parent.Length] == Path.DirectorySeparatorChar &&
         path.StartsWith(parent, StringComparison.OrdinalIgnoreCase));
}

internal static class PortableSettings
{
    public static T? Load<T>(string fileName) where T : class
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
    }

    public static T Update<T>(string fileName, string mutexName, Func<T> load, Action<T> change)
    {
        using var mutex = new Mutex(false, mutexName);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("设置正在被另一个窗口更新，请再试一次。");

            var data = load();
            change(data);
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return data;
        }
        finally
        {
            if (entered) mutex.ReleaseMutex();
        }
    }
}
