using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Sovereign.Desktop.Connector;
using Windows.Foundation;

namespace Sovereign.Desktop;

public static class GrokbotAvatar
{
    public static Grid Mark(string? color, string? shape, double size)
    {
        var paint = GrokbotAvatarPaint.Create(color, shape, size);
        var fill = MainWindow.Ink(paint.R, paint.G, paint.B);
        var eyeFill = MainWindow.Ink(paint.EyeR, paint.EyeG, paint.EyeB);
        var face = new Canvas { Width = paint.Size, Height = paint.Size };
        face.RenderTransform = new RotateTransform
        {
            Angle = paint.Tilt,
            CenterX = paint.Size / 2,
            CenterY = paint.Size / 2
        };
        DrawBody(face, paint, fill);
        foreach (var eye in paint.Steps)
        {
            if (eye.Op != GrokbotDrawOp.Eye) continue;
            var mark = new Ellipse { Width = eye.Width, Height = eye.Height, Fill = eyeFill };
            Canvas.SetLeft(mark, eye.X);
            Canvas.SetTop(mark, eye.Y);
            face.Children.Add(mark);
        }
        var grid = new Grid { Width = paint.Size, Height = paint.Size, Children = { face } };
        AutomationProperties.SetName(grid, "Grokbot");
        return grid;
    }

    private static void DrawBody(Canvas canvas, GrokbotAvatarPaint paint, Brush fill)
    {
        GrokbotDrawStep? ellipse = null;
        foreach (var step in paint.Steps)
            if (step.Op == GrokbotDrawOp.Ellipse) ellipse = step;
        if (ellipse is { } body)
        {
            var shape = new Ellipse { Width = body.Width, Height = body.Height, Fill = fill };
            Canvas.SetLeft(shape, body.X);
            Canvas.SetTop(shape, body.Y);
            canvas.Children.Add(shape);
            return;
        }
        var figure = new PathFigure { IsClosed = true };
        var started = false;
        foreach (var step in paint.Steps)
        {
            if (step.Op == GrokbotDrawOp.Move)
            {
                figure.StartPoint = new Point(step.X, step.Y);
                started = true;
            }
            else if (step.Op == GrokbotDrawOp.Line)
                figure.Segments.Add(new LineSegment { Point = new Point(step.X, step.Y) });
            else if (step.Op == GrokbotDrawOp.Curve)
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = new Point(step.ControlX, step.ControlY),
                    Point2 = new Point(step.X, step.Y)
                });
        }
        if (!started) return;
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = fill });
    }
}
