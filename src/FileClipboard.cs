using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickFileDesk;

internal sealed record ClipboardFiles(string[] Paths, bool Move, uint Sequence);

internal static class FileClipboard
{
    private const string PreferredEffect = "Preferred DropEffect";

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public static void Write(string[] paths, bool move)
    {
        var list = new StringCollection();
        list.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(list);
        using var effect = new MemoryStream(BitConverter.GetBytes(move ? 2 : 1));
        data.SetData(PreferredEffect, effect, autoConvert: false);
        Clipboard.SetDataObject(data, copy: true);
    }

    public static ClipboardFiles? Read()
    {
        var sequence = GetClipboardSequenceNumber();
        var data = Clipboard.GetDataObject();
        if (data?.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] paths || paths.Length == 0)
            return null;
        var move = false;
        if (data.GetData(PreferredEffect, autoConvert: false) is Stream effect)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (effect.CanSeek) effect.Position = 0;
            move = effect.Read(bytes) == 4 && BitConverter.ToInt32(bytes) == 2;
        }
        return new(paths, move, sequence);
    }

    public static void CompleteMove(ClipboardFiles original, OperationResult result)
    {
        if (!original.Move || result.Completed == 0 || GetClipboardSequenceNumber() != original.Sequence)
            return;
        if (result.Remaining.Length == 0) Clipboard.Clear();
        else Write(result.Remaining, move: true);
    }
}
