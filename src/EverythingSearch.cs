using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace QuickFileDesk;

internal sealed record IndexedHit(string Name, string Folder, bool IsFolder, long Bytes, DateTime? Modified)
{
    public string FullPath => Folder.Length == 0 ? Name : Path.Combine(Folder, Name);
    public string UpdatedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string Size => IsFolder ? "" : Bytes < 0 ? "—" : BrowserData.FormatSize(Bytes);
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
}

internal sealed record IndexedPage(int? Total, IndexedHit[] Hits, int? NextOffset);

internal sealed class EverythingSearch : IDisposable
{
    internal const int PageSize = 200;
    // Query2W and its result layout are defined in the official everything_ipc.h.
    private const int CopyDataMessage = 0x004A, IpcMessage = 0x0400;
    private const int IsDbLoaded = 401, Query2W = 18, NameAscending = 1;
    private const int NameFlag = 1, PathFlag = 2, SizeFlag = 0x10, ModifiedFlag = 0x40;
    private const int RequestFlags = NameFlag | PathFlag | SizeFlag | ModifiedFlag;
    private const int AbortHung = 0x0003; // SMTO_BLOCK | SMTO_ABORTIFHUNG
    private readonly HwndSource replyWindow;
    private int replyId;
    private TaskCompletionSource<IndexedPage>? pending;

    public EverythingSearch()
    {
        replyWindow = new HwndSource(new HwndSourceParameters("PathDeck Everything")
        {
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        });
        replyWindow.AddHook(Receive);
    }

    public static bool IsRunning => FindServer() != IntPtr.Zero;

    public async Task<IndexedPage> Query(string term, string filter, string? drive, int offset, CancellationToken cancellation)
    {
        // Everything responds later by sending WM_COPYDATA to the reply window.
        var reply = new TaskCompletionSource<IndexedPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = unchecked(++replyId);
        pending = reply;
        var hwnd = replyWindow.Handle;
        try
        {
            await Task.Run(() => Send(hwnd, id, term, filter, drive, offset, cancellation), cancellation).WaitAsync(cancellation);
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation);
        }
        finally
        {
            if (pending == reply) pending = null;
        }
    }

    private static void Send(IntPtr hwnd, int id, string term, string filter, string? drive, int offset, CancellationToken cancellation)
    {
        var server = FindServer();
        if (server == IntPtr.Zero)
            throw new IOException("Everything 未在后台运行。请打开 Everything，再按回车重试。");
        CheckSend(SendMessageTimeout(server, IpcMessage, new IntPtr(IsDbLoaded), IntPtr.Zero, AbortHung, 1200, out var ready));
        if (ready == IntPtr.Zero) throw new IOException("Everything 正在加载索引，稍后按回车重试。");

        var scope = drive is null ? "" : $"{drive.TrimEnd('\\')} ";
        var query = Encoding.Unicode.GetBytes($"{filter}{scope}\"{term}\"\0");
        var bytes = new byte[28 + query.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, unchecked((int)hwnd.ToInt64()));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), PageSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), RequestFlags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), NameAscending);
        query.CopyTo(bytes, 28);
        cancellation.ThrowIfCancellationRequested();

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var data = new CopyData { Id = new IntPtr(Query2W), Length = bytes.Length, Buffer = buffer };
            CheckSend(SendMessageTimeout(server, CopyDataMessage, hwnd, ref data, AbortHung, 1200, out var accepted));
            if (accepted == IntPtr.Zero) throw new IOException("Everything 未接受搜索请求。请确认客户端支持本机搜索接口。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void CheckSend(IntPtr sent)
    {
        if (sent != IntPtr.Zero) return;
        if (Marshal.GetLastPInvokeError() == 5)
            throw new IOException("无法连接 Everything。请让 PathDeck 和 Everything 使用相同的权限运行。");
        throw new TimeoutException();
    }

    private IntPtr Receive(IntPtr window, int message, IntPtr sender, IntPtr dataPointer, ref bool handled)
    {
        if (message != CopyDataMessage || pending is not { } reply || dataPointer == IntPtr.Zero) return IntPtr.Zero;
        var data = Marshal.PtrToStructure<CopyData>(dataPointer);
        if (data.Id.ToInt64() != replyId) return IntPtr.Zero;
        handled = true;
        try
        {
            if (data.Length is < 20 or > 16 * 1024 * 1024 || data.Buffer == IntPtr.Zero) throw new InvalidDataException();
            var bytes = new byte[data.Length];
            Marshal.Copy(data.Buffer, bytes, 0, bytes.Length);
            reply.TrySetResult(ReadPage(bytes));
        }
        catch (InvalidDataException)
        {
            reply.TrySetException(new IOException("无法读取 Everything 返回的结果。"));
        }
        return new IntPtr(1);
    }

    private static IntPtr FindServer()
    {
        var server = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        return server != IntPtr.Zero ? server : FindWindow("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", null);
    }

    private static IndexedPage ReadPage(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var total = reader.ReadInt32();
        var count = reader.ReadInt32();
        var pageOffset = reader.ReadInt32();
        var flags = reader.ReadInt32();
        if (count is < 0 or > PageSize || total < count || pageOffset < 0 || 20 + 8 * count > bytes.Length ||
            (flags & (NameFlag | PathFlag)) != (NameFlag | PathFlag) || (flags & ~RequestFlags) != 0)
            throw new InvalidDataException();

        var hits = new IndexedHit[count];
        for (var i = 0; i < count; i++)
        {
            reader.BaseStream.Position = 20 + 8 * i;
            var isFolder = (reader.ReadInt32() & 3) != 0;
            var offset = reader.ReadInt32();
            if (offset < 20 + 8 * count || offset > bytes.Length) throw new InvalidDataException();
            reader.BaseStream.Position = offset;
            var name = ReadText(reader, bytes);
            var folder = ReadText(reader, bytes);
            var metadataSize = ((flags & SizeFlag) != 0 ? 8 : 0) + ((flags & ModifiedFlag) != 0 ? 8 : 0);
            if (reader.BaseStream.Position + metadataSize > bytes.Length) throw new InvalidDataException();
            var size = (flags & SizeFlag) != 0 ? reader.ReadInt64() : -1;
            var timestamp = (flags & ModifiedFlag) != 0 ? reader.ReadInt64() : 0;
            var modified = timestamp is > 0 and <= 2650467743999999999L ? DateTime.FromFileTime(timestamp) : (DateTime?)null;
            hits[i] = new IndexedHit(name, folder, isFolder, size, modified);
        }
        return new IndexedPage(total, hits, count > 0 && (long)pageOffset + count < total ? pageOffset + count : null);
    }

    private static string ReadText(BinaryReader reader, byte[] bytes)
    {
        if (reader.BaseStream.Position + 4 > bytes.Length) throw new InvalidDataException();
        var chars = reader.ReadInt32();
        var start = (int)reader.BaseStream.Position;
        var end = start + (chars + 1L) * 2;
        if (chars < 0 || end > bytes.Length) throw new InvalidDataException();
        reader.BaseStream.Position = end;
        return Encoding.Unicode.GetString(bytes, start, chars * 2);
    }

    public void Dispose() => replyWindow.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public IntPtr Id;
        public int Length;
        public IntPtr Buffer;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, ref CopyData data, int flags, int timeout, out IntPtr result);
}
