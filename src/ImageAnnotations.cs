using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuickFileDesk;

internal enum ImageTool { Pan, Pen, Line, Arrow, Rectangle, Ellipse, Measure }

internal sealed class ImageEdits
{
    private ImageMark[] saved = [];
    public List<ImageMark> Marks { get; } = [];
    public Stack<ImageMark> Undone { get; } = new();
    public bool HasUnsavedMarks => Marks.Count > 0 && !Marks.SequenceEqual(saved);

    public void MarkSaved(ImageMark[] marks) => saved = marks;
}

internal sealed class ImageMark(ImageTool tool, Color color, double width, Point start)
{
    private readonly Point startPoint = start;
    private Point end = start;
    private readonly List<Point> points = [start];
    private Vector Delta => end - startPoint;
    private Rect Bounds => new(startPoint, end);
    private Rect PixelRegion => new(Bounds.X - .5, Bounds.Y - .5, Bounds.Width + 1, Bounds.Height + 1);

    public bool CanCommit => tool == ImageTool.Pen || Delta.Length > 0;

    public string Readout => tool switch
    {
        ImageTool.Measure => FormattableString.Invariant($"距离 {Delta.Length:0.00} px · ΔX {Delta.X:0} · ΔY {Delta.Y:0}"),
        ImageTool.Rectangle or ImageTool.Ellipse =>
            FormattableString.Invariant($"范围 ({PixelRegion.X:0}, {PixelRegion.Y:0}) · {PixelRegion.Width:0} × {PixelRegion.Height:0} px"),
        _ => ""
    };

    public void Extend(Point point)
    {
        end = point;
        if (tool == ImageTool.Pen && points[^1] != point) points.Add(point);
    }

    public void Draw(DrawingContext drawing, Rect imageBounds, double minStroke, double labelSize, double pixelsPerDip)
    {
        var thickness = Math.Max(width, minStroke);
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        switch (tool)
        {
            case ImageTool.Pen:
                if (points.Count == 1)
                {
                    drawing.DrawEllipse(brush, null, startPoint, thickness / 2, thickness / 2);
                    break;
                }
                var path = new StreamGeometry();
                using (var geometry = path.Open())
                {
                    geometry.BeginFigure(startPoint, false, false);
                    geometry.PolyLineTo(points, true, false);
                }
                drawing.DrawGeometry(null, pen, path);
                break;
            case ImageTool.Line:
                drawing.DrawLine(pen, startPoint, end);
                break;
            case ImageTool.Arrow:
                drawing.DrawLine(pen, startPoint, end);
                var direction = Delta;
                var length = direction.Length;
                if (length <= 0) break;
                direction.Normalize();
                var headLength = Math.Min(length * .45, Math.Max(8, thickness * 3));
                var back = end - direction * headLength;
                var side = new Vector(-direction.Y, direction.X) * (headLength * .48);
                drawing.DrawLine(pen, end, back + side);
                drawing.DrawLine(pen, end, back - side);
                break;
            case ImageTool.Rectangle:
                drawing.DrawRectangle(null, pen, PixelRegion);
                break;
            case ImageTool.Ellipse:
                drawing.DrawEllipse(null, pen, startPoint + Delta / 2, PixelRegion.Width / 2, PixelRegion.Height / 2);
                break;
            case ImageTool.Measure:
                pen.DashStyle = DashStyles.Dash;
                drawing.DrawLine(pen, startPoint, end);
                drawing.DrawEllipse(brush, null, startPoint, thickness, thickness);
                drawing.DrawEllipse(brush, null, end, thickness, thickness);
                DrawDistance(drawing, imageBounds, labelSize, pixelsPerDip);
                break;
        }
    }

    private void DrawDistance(DrawingContext drawing, Rect bounds, double fontSize, double pixelsPerDip)
    {
        fontSize = Math.Min(fontSize, Math.Min(bounds.Width, bounds.Height) / 5);
        if (fontSize < 1) return;
        var label = new FormattedText(FormattableString.Invariant($"{Delta.Length:0.00} px"),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize,
            Brushes.White, pixelsPerDip);
        var padding = fontSize / 4;
        var width = label.Width + padding * 2;
        var height = label.Height + padding * 2;
        if (width > bounds.Width || height > bounds.Height) return;
        var middle = startPoint + Delta / 2;
        var x = Math.Clamp(middle.X - width / 2, 0, bounds.Width - width);
        var y = Math.Clamp(middle.Y - height - padding, 0, bounds.Height - height);
        drawing.DrawRoundedRectangle(Brushes.Black, null, new Rect(x, y, width, height), padding, padding);
        drawing.DrawText(label, new Point(x + padding, y + padding));
    }
}
