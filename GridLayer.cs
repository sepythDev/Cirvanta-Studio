using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitForge;

public sealed class GridLayer : Control
{
    private readonly double _width;
    private readonly double _height;
    private readonly IBrush _brush;
    private readonly double _baseStep;
    private double _zoom = 1.0;

    public GridLayer(double width, double height, IBrush brush, double thickness, double step)
    {
        _width = width;
        _height = height;
        _brush = brush;
        _baseStep = step;
        Width = width;
        Height = height;
        IsHitTestVisible = false;
    }

    public void SetZoom(double zoom)
    {
        if (Math.Abs(_zoom - zoom) < 0.01) return;
        _zoom = zoom;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // При сильном отдалении удваиваем шаг, чтобы точки не сливались и не рябили
        var step = _zoom < 0.65 ? _baseStep * 2 : _baseStep;
        var dotSize = Math.Clamp(1.5 / Math.Max(0.35, _zoom), 1.4, 3.0);
        var gap = Math.Max(1.0, step - dotSize);

        var pen = new Pen(
            _brush,
            dotSize,
            new DashStyle(new[] { 1.0, gap / dotSize }, 0),
            PenLineCap.Flat);

        var startX = step - dotSize / 2.0;
        var endX = _width - step + dotSize / 2.0;

        for (double y = step; y < _height; y += step)
        {
            context.DrawLine(pen, new Point(startX, y), new Point(endX, y));
        }
    }
}