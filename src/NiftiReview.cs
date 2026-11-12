using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickFileDesk;

internal sealed record ReviewItem(FileEntry Entry, FileEntry? Mask = null, FileEntry? Png = null)
{
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
}

internal static class NiftiFiles
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    public static bool IsNifti(string name) => name.EndsWith(".nii", StringComparison.OrdinalIgnoreCase) || IsCompressed(name);
    private static bool IsCompressed(string name) => name.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase);
    private static string Stem(string name) => IsCompressed(name) ? name[..^7] : Path.GetFileNameWithoutExtension(name);

    public static bool IsReviewable(FileEntry item) => !item.IsDirectory &&
        (item.IsImage || IsNifti(item.Name));

    private static bool NamedMask(FileEntry item) => IsNifti(item.Name) &&
        (Stem(item.Name).EndsWith("_seg", StringComparison.OrdinalIgnoreCase) ||
         Stem(item.Name).EndsWith("_mask", StringComparison.OrdinalIgnoreCase));

    public static string CaseKey(FileEntry item)
    {
        var name = Stem(item.Name);
        if (NamedMask(item)) name = name[..name.LastIndexOf('_')];
        return Path.Combine(Path.GetDirectoryName(item.FullPath) ?? "", name);
    }

    public static Dictionary<string, ReviewItem> Pairs(IEnumerable<FileEntry> entries)
    {
        var pairs = new Dictionary<string, ReviewItem>(Paths);
        var candidates = entries.Where(item => !item.IsDirectory &&
            (IsNifti(item.Name) || item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
        foreach (var group in candidates.DistinctBy(item => item.FullPath, Paths).GroupBy(CaseKey, Paths))
        {
            var volumes = group.Where(item => IsNifti(item.Name) && !NamedMask(item)).ToArray();
            var masks = group.Where(NamedMask).ToArray();
            var png = group.FirstOrDefault(item => item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            ReviewItem? pair = null;
            if (volumes.Length == 1 && masks.Length == 1)
                pair = new ReviewItem(volumes[0], masks[0], png);
            else if (masks.Length == 0 && volumes.Length == 2)
            {
                var source = volumes.FirstOrDefault(item => !IsCompressed(item.Name));
                var mask = volumes.FirstOrDefault(item => IsCompressed(item.Name));
                if (source is not null && mask is not null) pair = new ReviewItem(source, mask, png);
            }
            if (pair is null) continue;
            pairs[pair.FullPath] = pair;
            pairs[pair.Mask!.FullPath] = pair;
            if (png is not null) pairs[png.FullPath] = pair;
        }
        return pairs;
    }

    public static bool IsMatchDependency(FileEntry selected, FileEntry changed) =>
        !selected.IsDirectory && !changed.IsDirectory && Paths.Equals(CaseKey(selected), CaseKey(changed)) &&
        (IsNifti(selected.Name) || selected.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) &&
        IsNifti(changed.Name);

    public static NiftiReview Load(ReviewItem item, CancellationToken token)
    {
        var plane = NiftiPlane.Read(item.FullPath, token);
        var bitmap = plane.CreateBitmap(token);
        NiftiLabels? labels = null;
        var status = NamedMask(item.Entry) ? "仅显示标签文件 · 未找到配对原图" : "NIfTI 原图 · 未找到同名标注";
        if (item.Mask is not null)
        {
            try
            {
                var mask = NiftiPlane.Read(item.Mask.FullPath, token);
                if (!plane.IsAlignedWith(mask))
                    status = "原图与标注的尺寸或空间信息不匹配，已停用叠加。";
                else if (!NamedMask(item.Mask) && plane.SameVoxels(mask))
                    status = "同名文件的像素相同，已按原图的压缩副本处理。";
                else
                {
                    labels = NiftiLabels.Read(mask, token);
                    status = labels.Classes.Length == 0 ? "标注为空" : $"标注 {labels.Classes.Length} 类";
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FormatException)
            {
                status = $"无法叠加标注 · {ex.Message}";
            }
        }
        return new NiftiReview(plane, bitmap, labels, status);
    }
}

internal sealed class NiftiReview(NiftiPlane plane, BitmapSource image, NiftiLabels? labels, string status)
{
    public BitmapSource Image { get; } = image;
    public PreviewPixels Dimensions { get; } = new(plane.Width, plane.Height);
    public NiftiLabels? Labels { get; } = labels;
    public string Status { get; } = status;

    public string PixelAt(int x, int y)
    {
        var index = y * plane.Width + x;
        var value = FormattableString.Invariant($"灰度 {plane.ValueAt(index):G9}");
        return Labels is null ? value : $"{value} · 标签 {Labels.Values[index]}";
    }

    public BitmapSource Preview(CancellationToken token) =>
        Labels is { Classes.Length: > 0 } layer ? layer.MakeBitmap(null, token, Image) : Image;
}

internal sealed class NiftiLabels(int width, int height, int[] values, int[] classes)
{
    public int[] Values { get; } = values;
    public int[] Classes { get; } = classes;

    public static NiftiLabels Read(NiftiPlane plane, CancellationToken token)
    {
        var values = new int[plane.Width * plane.Height];
        var classes = new HashSet<int>();
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            var value = plane.ValueAt(index);
            if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
                throw new FormatException("标注应使用非负整数标签。");
            var label = values[index] = (int)value;
            if (label > 0 && classes.Add(label) && classes.Count > 256)
                throw new NotSupportedException("标签类别超过 256 个，请确认已选择分割文件。");
        }
        return new NiftiLabels(plane.Width, plane.Height, values, classes.Order().ToArray());
    }

    public static Color ClassColor(int label)
    {
        if (label == 1) return Color.FromRgb(255, 112, 66);
        if (label == 2) return Color.FromRgb(49, 211, 232);
        if (label == 3) return Color.FromRgb(140, 239, 88);
        var hue = label * 0.618033988749895 % 1 * 6;
        var middle = .72 * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (.72, middle, 0d), 1 => (middle, .72, 0d), 2 => (0d, .72, middle),
            3 => (0d, middle, .72), 4 => (middle, 0d, .72), _ => (.72, 0d, middle)
        };
        return Color.FromRgb((byte)((r + .24) * 255), (byte)((g + .24) * 255), (byte)((b + .24) * 255));
    }

    public BitmapSource MakeBitmap(IReadOnlySet<int>? hidden, CancellationToken token, BitmapSource? baseImage = null)
    {
        var colors = Classes.Where(label => hidden?.Contains(label) != true).ToDictionary(label => label, ClassColor);
        var gray = baseImage is null ? null : new byte[Values.Length];
        if (gray is not null) baseImage!.CopyPixels(gray, width, 0);
        var buffer = new byte[Values.Length * 4];
        for (var index = 0; index < Values.Length; index++)
        {
            if ((index & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            var offset = index * 4;
            if (gray is not null)
            {
                buffer[offset] = buffer[offset + 1] = buffer[offset + 2] = gray[index];
                buffer[offset + 3] = 255;
            }
            if (Values[index] == 0 || !colors.TryGetValue(Values[index], out var color)) continue;
            buffer[offset] = gray is null ? color.B : (byte)((color.B + gray[index]) / 2);
            buffer[offset + 1] = gray is null ? color.G : (byte)((color.G + gray[index]) / 2);
            buffer[offset + 2] = gray is null ? color.R : (byte)((color.R + gray[index]) / 2);
            buffer[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class NiftiPlane(int width, int height, int type, int bytes, bool littleEndian, byte[] data,
    double slope, double intercept, int units, short qCode, short sCode, float[] qForm, float[] sForm)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    public static NiftiPlane Read(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : null;
        Stream input = gzip is null ? file : gzip;
        var header = new byte[348];
        input.ReadExactly(header);
        var little = BinaryPrimitives.ReadInt32LittleEndian(header) == 348;
        int Int32At(int offset) => little ? BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset)) :
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(offset));
        short Int16At(int offset) => little ? BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(offset)) :
            BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(offset));
        float FloatAt(int offset) => BitConverter.Int32BitsToSingle(Int32At(offset));
        if (Int32At(0) != 348 || !header.AsSpan(344, 4).SequenceEqual("n+1\0"u8))
            throw new NotSupportedException("需要单文件 NIfTI-1 图像。");

        var dimensions = Int16At(40);
        var w = Int16At(42);
        var h = Int16At(44);
        if (dimensions < 2 || dimensions > 7 || w <= 0 || h <= 0 ||
            Enumerable.Range(3, dimensions - 2).Any(axis => Int16At(40 + axis * 2) != 1))
            throw new NotSupportedException("当前支持单张切片，暂未支持多切片体数据。");
        var datatype = Int16At(70);
        var elementBytes = datatype switch
        {
            2 or 256 => 1, 4 or 512 => 2, 8 or 16 or 768 => 4, 64 => 8,
            _ => throw new NotSupportedException("暂不支持此 NIfTI 像素类型。")
        };
        var pixels = (long)w * h;
        if (Int16At(72) != elementBytes * 8 || pixels > 16_000_000 || pixels * elementBytes > 64 * 1024 * 1024)
            throw new NotSupportedException("像素格式不一致，或图像超过当前查看上限。");
        var offset = FloatAt(108);
        if (!float.IsFinite(offset) || offset < 352 || offset > 16 * 1024 * 1024 || offset != Math.Truncate(offset))
            throw new FormatException("NIfTI 数据偏移无效。");

        Span<byte> discard = stackalloc byte[8192];
        for (var remaining = (int)offset - header.Length; remaining > 0;)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(remaining, discard.Length);
            input.ReadExactly(discard[..length]);
            remaining -= length;
        }
        var data = new byte[(int)(pixels * elementBytes)];
        for (var start = 0; start < data.Length; start += 65536)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(data.AsSpan(start, Math.Min(65536, data.Length - start)));
        }
        var factor = FloatAt(112);
        var bias = FloatAt(116);
        if (factor == 0 || !float.IsFinite(factor)) { factor = 1; bias = 0; }
        else if (!float.IsFinite(bias)) throw new FormatException("灰度缩放参数无效。");
        var q = Enumerable.Range(0, 4).Select(i => FloatAt(76 + i * 4))
            .Concat(Enumerable.Range(0, 6).Select(i => FloatAt(256 + i * 4))).ToArray();
        var s = Enumerable.Range(0, 12).Select(i => FloatAt(280 + i * 4)).ToArray();
        return new NiftiPlane(w, h, datatype, elementBytes, little, data, factor, bias, header[123] & 7,
            Int16At(252), Int16At(254), q, s);
    }

    public bool IsAlignedWith(NiftiPlane other)
    {
        if (Width != other.Width || Height != other.Height || units != other.Units) return false;
        if (sCode > 0 || other.SCode > 0)
            return sCode > 0 && sCode == other.SCode && HasAffine(sForm) && Same(sForm, other.SForm) &&
                (qCode <= 0 || other.QCode <= 0 || qCode == other.QCode && Same(qForm, other.QForm));
        return qCode > 0 && qCode == other.QCode && qForm[1] > 0 && qForm[2] > 0 && qForm[3] > 0 && Same(qForm, other.QForm);
    }

    private int Units => units;
    private short QCode => qCode;
    private short SCode => sCode;
    private float[] QForm => qForm;
    private float[] SForm => sForm;
    private int DataType => type;
    private double Slope => slope;
    private double Intercept => intercept;
    private byte[] Data => data;

    public bool SameVoxels(NiftiPlane other) => type == other.DataType && slope == other.Slope &&
        intercept == other.Intercept && data.AsSpan().SequenceEqual(other.Data);

    private static bool Same(float[] left, float[] right) =>
        left.Zip(right).All(pair => float.IsFinite(pair.First) && float.IsFinite(pair.Second) && Math.Abs(pair.First - pair.Second) <= 0.00001f);

    private static bool HasAffine(float[] m)
    {
        var determinant = (double)m[0] * (m[5] * m[10] - m[6] * m[9]) -
            (double)m[1] * (m[4] * m[10] - m[6] * m[8]) + (double)m[2] * (m[4] * m[9] - m[5] * m[8]);
        return double.IsFinite(determinant) && Math.Abs(determinant) > 1e-18;
    }

    public double ValueAt(int index)
    {
        var sample = data.AsSpan(index * bytes, bytes);
        double value = type switch
        {
            2 => sample[0],
            256 => (sbyte)sample[0],
            4 => littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(sample) : BinaryPrimitives.ReadInt16BigEndian(sample),
            512 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(sample) : BinaryPrimitives.ReadUInt16BigEndian(sample),
            8 => littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(sample) : BinaryPrimitives.ReadInt32BigEndian(sample),
            768 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(sample) : BinaryPrimitives.ReadUInt32BigEndian(sample),
            16 => littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(sample) : BinaryPrimitives.ReadSingleBigEndian(sample),
            _ => littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(sample) : BinaryPrimitives.ReadDoubleBigEndian(sample)
        };
        return value * slope + intercept;
    }

    public BitmapSource CreateBitmap(CancellationToken token)
    {
        var gray = data;
        if (type != 2 || slope != 1 || intercept != 0)
        {
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            gray = new byte[Width * Height];
            for (var i = 0; i < gray.Length; i++)
            {
                if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                var value = ValueAt(i);
                if (!double.IsFinite(value)) continue;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            var range = max - min;
            if (double.IsFinite(range) && range > 0)
                for (var i = 0; i < gray.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                    var value = ValueAt(i);
                    if (double.IsFinite(value)) gray[i] = (byte)Math.Clamp((value - min) / range * 255, 0, 255);
                }
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, gray, Width);
        bitmap.Freeze();
        return bitmap;
    }
}
