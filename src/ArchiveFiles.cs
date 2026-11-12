using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickFileDesk;

internal readonly record struct ArchiveProgress(long? WrittenBytes, long? StartedEntries);

internal static class ArchiveFiles
{
    private const string StagingPrefix = ".1Commander-archive-";
    private static readonly string[] Extensions =
        [".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz", ".zip", ".7z", ".rar", ".tar"];
    private static readonly string Tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
    private static readonly string Rar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "Rar.exe");
    public static bool CanCreateRar => File.Exists(Rar);

    public static string? GetExtension(string name) =>
        Extensions.FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static bool IsStagingPath(string path)
    {
        var offset = 0;
        while ((offset = path.IndexOf(StagingPrefix, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var guidStart = offset + StagingPrefix.Length;
            var guidEnd = guidStart + 32;
            if ((offset == 0 || path[offset - 1] == '\\') && guidEnd <= path.Length &&
                (guidEnd == path.Length || path[guidEnd] == '\\') &&
                Guid.TryParseExact(path.AsSpan(guidStart, 32), "N", out _))
                return true;
            offset = guidStart;
        }
        return false;
    }

    public static string Compress(string directory, string[] paths, string name, string extension,
        CancellationToken token, IProgress<ArchiveProgress>? progress)
    {
        FileOperations.ValidateName(name);
        token.ThrowIfCancellationRequested();
        var selected = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();
        foreach (var path in selected)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, path);
            if (relative is "." or ".." || relative.StartsWith(@"..\") || Path.IsPathFullyQualified(relative))
                throw new IOException("请选择当前文件夹里的项目。");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"无法压缩链接项目「{Path.GetFileName(path)}」。");

            var covered = false;
            for (var parent = Path.GetDirectoryName(path); parent is not null && !FileOperations.SamePath(parent, directory);
                parent = Path.GetDirectoryName(parent))
            {
                if (!selected.Contains(parent)) continue;
                covered = true;
                break;
            }
            if (!covered) entries.Add(relative);
        }

        if (entries.Count == 0) throw new IOException("请选择要压缩的文件或文件夹。");
        var output = FileOperations.AvailableName(directory, name, folder: false, extensionOverride: extension);
        var staging = CreateStagingDirectory(directory);
        try
        {
            var archive = Path.Combine(staging, "archive" + extension);
            var list = Path.Combine(staging, "items.txt");
            if (extension == ".rar")
            {
                File.WriteAllLines(list, entries, Encoding.Unicode);
                Run(Rar, directory, ["a", "-ma5", "-idq", "-scul", archive, "@" + list], archive, token, progress);
            }
            else
            {
                var relativeNames = entries.Select(entry => "./" + entry.Replace('\\', '/')).ToArray();
                var arguments = new List<string> { "-c", "-a", "-f", archive };
                if (relativeNames.Sum(entry => (long)entry.Length + 3) < 24000)
                {
                    arguments.Add("--");
                    arguments.AddRange(relativeNames);
                }
                else
                {
                    var encoding = AnsiEncoding(EncoderFallback.ExceptionFallback);
                    try { File.WriteAllBytes(list, encoding.GetBytes(string.Join('\0', relativeNames) + "\0")); }
                    catch (EncoderFallbackException)
                    {
                        throw new IOException("文件名中有系统编码不支持的字符，请分批压缩这些项目。");
                    }
                    arguments.AddRange(["--null", "-T", list]);
                }
                Run(Tar, directory, arguments, archive, token, progress);
            }
            token.ThrowIfCancellationRequested();
            File.Move(archive, output);
            return output;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    public static string Extract(string archive, CancellationToken token, IProgress<ArchiveProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        var name = Path.GetFileName(archive);
        var extension = GetExtension(name) ?? throw new IOException("此压缩包格式暂不支持。");
        var directory = Path.GetDirectoryName(archive)!;
        var stem = name[..^extension.Length].TrimEnd(' ', '.');
        var output = FileOperations.AvailableName(directory, stem.Length == 0 ? "解压文件" : stem, folder: true);
        var staging = CreateStagingDirectory(directory);
        try
        {
            var content = Path.Combine(staging, "content");
            Directory.CreateDirectory(content);
            Run(Tar, directory, ["-x", "-v", "-k", "--no-same-owner", "--no-same-permissions", "-f", archive, "-C", content],
                null, token, progress);
            var directories = new Stack<string>();
            directories.Push(content);
            while (directories.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("压缩包包含符号链接或目录联接，已停止解压。");
                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                }
            }

            token.ThrowIfCancellationRequested();
            Directory.Move(content, output);
            return output;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static string CreateStagingDirectory(string directory)
    {
        var path = Path.Combine(directory, StagingPrefix + Guid.NewGuid().ToString("N"));
        var staging = Directory.CreateDirectory(path);
        try { staging.Attributes |= FileAttributes.Hidden; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some mapped drives do not support changing attributes. The app still filters staging paths.
        }
        return path;
    }

    private static void Run(string executable, string directory, IEnumerable<string> arguments, string? writingArchive,
        CancellationToken token, IProgress<ArchiveProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new IOException("系统中没有找到所需的归档程序。");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = AnsiEncoding()
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("无法启动归档程序。");
            process.StandardInput.Close();
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            long entries = 0;
            var errors = ReadArchiveMessages(process.StandardError, () =>
            {
                if (Interlocked.Increment(ref entries) == 1) progress?.Report(new ArchiveProgress(null, 1));
            }, writingArchive is null);
            try
            {
                while (!process.WaitForExit(180))
                {
                    token.ThrowIfCancellationRequested();
                    if (progress is null) continue;
                    var current = writingArchive is null ? new ArchiveProgress(null, Interlocked.Read(ref entries))
                        : new ArchiveProgress(GetWrittenLength(writingArchive), null);
                    progress.Report(current);
                }
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit();
                Task.WaitAll(output, errors);
                throw;
            }
            Task.WaitAll(output, errors);
            if (process.ExitCode != 0)
                throw new IOException(errors.Result ?? $"归档程序返回错误 {process.ExitCode}。");
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException(ex.Message, ex); }
    }

    private static async Task<string?> ReadArchiveMessages(StreamReader reader, Action startedEntry, bool extracting)
    {
        string? firstError = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (extracting && line.StartsWith("x ", StringComparison.Ordinal))
                startedEntry();
            else if (!string.IsNullOrWhiteSpace(line))
                firstError ??= line.Trim();
        }
        return firstError;
    }

    private static long GetWrittenLength(string archive)
    {
        try { return new FileInfo(archive).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static Encoding AnsiEncoding(EncoderFallback? fallback = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetACP(), fallback ?? EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

