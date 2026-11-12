using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace QuickFileDesk;

internal sealed record OperationResult(int Completed, string[] Remaining, string[] Errors, Dictionary<string, string>? Moves = null);

internal static class FileOperations
{
    public static OperationResult Paste(string[] paths, string destination, bool move)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException("目标文件夹无法访问。");

        var moves = move ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
        var completed = 0;
        var remaining = new List<string>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path))
                    throw new IOException("剪贴板中有无效的文件路径。");

                var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var name = Path.GetFileName(source);
                if (name.Length == 0)
                    throw new IOException("请选择驱动器内的文件或文件夹。");

                var isDirectory = (File.GetAttributes(source) & FileAttributes.Directory) != 0;
                if (isDirectory && (SamePath(source, destination) ||
                    destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("不能将文件夹复制或移动到它自己里面。");

                var target = Path.Combine(destination, name);
                if (move && SamePath(source, target))
                {
                    remaining.Add(path);
                    continue;
                }

                target = AvailableName(destination, name, isDirectory);
                if (isDirectory)
                {
                    if (move)
                        FileSystem.MoveDirectory(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    else
                        FileSystem.CopyDirectory(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                else if (move)
                    File.Move(source, target);
                else
                    File.Copy(source, target);
                if (moves is not null) moves[source] = target;

                completed++;
            }
            catch (OperationCanceledException)
            {
                remaining.AddRange(paths.Skip(completed + remaining.Count));
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                remaining.Add(path);
                errors.Add($"{Path.GetFileName(path)} · {ex.Message}");
            }
        }
        return new(completed, remaining.ToArray(), errors.ToArray(), moves);
    }

    public static OperationResult Recycle(string[] paths)
    {
        var errors = new List<string>();
        var remaining = new List<string>();
        var completed = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path) || Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Length == 0)
                    throw new IOException("无法移动该项目到回收站。");
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                completed++;
            }
            catch (OperationCanceledException)
            {
                remaining.AddRange(paths.Skip(completed + remaining.Count));
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                remaining.Add(path);
                errors.Add($"{Path.GetFileName(path)} · {ex.Message}");
            }
        }
        return new(completed, remaining.ToArray(), errors.ToArray());
    }

    public static string AvailableName(string directory, string name, bool folder, string? extensionOverride = null)
    {
        var path = Path.Combine(directory, name);
        if (!Exists(path)) return path;
        var extension = folder ? "" : extensionOverride ?? Path.GetExtension(name);
        var stem = name[..(name.Length - extension.Length)];
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " - 副本" : $" - 副本 ({number})";
            path = Path.Combine(directory, stem + suffix + extension);
            if (!Exists(path)) return path;
        }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("请输入有效的文件名。");
    }

    public static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
