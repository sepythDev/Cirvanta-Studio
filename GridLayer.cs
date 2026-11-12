using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitForge;

/// <summary>
/// Draws the work-area grid as one retained geometry instead of hundreds of
/// individual Line controls. This keeps panning from traversing a large visual tree.
/// </summary>
internal sealed class GridLayer : Control
{
    private readonly StreamGeometry _geometry = new();
    private readonly Pen _pen;

    public GridLayer(double width, double height, IBrush stroke, double thickness, double spacing)
    {
        Width = width;
        Height = height;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _pen = new Pen(stroke, thickness);

        using var context = _geometry.Open();
        for (var x = 0.0; x < width; x += spacing)
        {
            context.BeginFigure(new Point(x, 0), isFilled: false);
            context.LineTo(new Point(x, height));
            context.EndFigure(isClosed: false);
        }

        for (var y = 0.0; y < height; y += spacing)
        {
            context.BeginFigure(new Point(0, y), isFilled: false);
            context.LineTo(new Point(width, y));
            context.EndFigure(isClosed: false);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawGeometry(null, _pen, _geometry);
    }
}
