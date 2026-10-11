using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Sovereign.Desktop.Client;
using Windows.Foundation;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

/// <summary>
/// Creature avatar drawn as geometry, not a stretched markup path.
/// A Path with Stretch.Fill was painting its layout slot as a solid blue square.
/// </summary>
internal sealed class SubagentBlobFace
{
    private readonly Microsoft.UI.Xaml.Shapes.Path _body = new() { Stretch = Stretch.None, Width = 36, Height = 36, IsHitTestVisible = false };
    private readonly Ellipse _eyeLeft = Eye();
    private readonly Ellipse _eyeRight = Eye();
    private readonly Ellipse _life = new()
    {
        Width = 9, Height = 9, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -DeskTheme.Space4, -DeskTheme.Space4), IsHitTestVisible = false
    };
    private readonly Storyboard _pulse = new();
    private int _shape = -1;
    private bool _loaded;
    private bool _running;
    private bool _pulseRunning;

    internal Grid Root { get; }
    internal string Key { get; private set; } = "";

    internal SubagentBlobFace()
    {
        _life.Stroke = DeskTheme.Surface;
        var eyes = new Canvas { Width = 36, Height = 36, IsHitTestVisible = false };
        eyes.Children.Add(_eyeLeft);
        eyes.Children.Add(_eyeRight);
        Root = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center, Children = { _body, eyes, _life } };
        Root.Loaded += (_, _) => { _loaded = true; UpdatePulse(); };
        Root.Unloaded += (_, _) => { _loaded = false; UpdatePulse(); };
        var pulse = new DoubleAnimation { From = 1, To = 0.4, Duration = new Duration(TimeSpan.FromMilliseconds(780)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, _life);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        _pulse.Children.Add(pulse);
        SetLife("idle");
    }

    internal void Apply(string? id, string? name)
    {
        var choice = SubagentBlobAvatar.Pick(id, name);
        Key = choice.Key;
        _body.Fill = Ink(choice.R, choice.G, choice.B);
        if (_shape == choice.ShapeIndex) return;
        _shape = choice.ShapeIndex;
        _body.Data = Body(choice.ShapeIndex);
        var eyes = EyeSpots[choice.ShapeIndex];
        Canvas.SetLeft(_eyeLeft, eyes.Left);
        Canvas.SetTop(_eyeLeft, eyes.Top);
        Canvas.SetLeft(_eyeRight, eyes.Right);
        Canvas.SetTop(_eyeRight, eyes.Top);
        AutomationName(Root, "Blob avatar " + choice.Key);
    }

    internal void SetLife(string? state)
    {
        var life = SubagentChatPanels.Life(state);
        _life.Fill = life == "running" ? DeskTheme.Success : life == "failed" ? DeskTheme.Error : DeskTheme.Muted;
        _running = life == "running";
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        if (_running && _loaded)
        {
            if (_pulseRunning) return;
            _pulse.Begin();
            _pulseRunning = true;
        }
        else
        {
            if (_pulseRunning) _pulse.Stop();
            _pulseRunning = false;
            _life.Opacity = 1;
        }
    }

    private static Ellipse Eye() => new() { Width = 4, Height = 4, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false };

    private static readonly (double Left, double Right, double Top)[] EyeSpots =
    [
        (11, 21, 14), (10, 20, 15), (12, 22, 13), (11, 21, 16)
    ];

    private static Geometry Body(int shape)
    {
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        foreach (var (start, a, b, end) in Curves[shape])
        {
            if (figure.Segments.Count == 0) figure.StartPoint = start;
            figure.Segments.Add(new BezierSegment { Point1 = a, Point2 = b, Point3 = end });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static readonly (Point Start, Point A, Point B, Point End)[][] Curves =
    [
        [
            (new(18, 3), new(28, 2), new(34, 10), new(33, 18)),
            (new(33, 18), new(34, 28), new(26, 35), new(17, 33)),
            (new(17, 33), new(7, 34), new(2, 26), new(4, 16)),
            (new(4, 16), new(2, 8), new(9, 2), new(18, 3))
        ],
        [
            (new(14, 6), new(22, 1), new(32, 6), new(31, 15)),
            (new(31, 15), new(36, 22), new(30, 34), new(18, 33)),
            (new(18, 33), new(8, 35), new(2, 26), new(5, 16)),
            (new(5, 16), new(1, 10), new(8, 6), new(14, 6))
        ],
        [
            (new(18, 2), new(26, 1), new(30, 8), new(27, 13)),
            (new(27, 13), new(35, 16), new(33, 28), new(24, 34)),
            (new(24, 34), new(16, 37), new(6, 30), new(6, 20)),
            (new(6, 20), new(2, 12), new(10, 3), new(18, 2))
        ],
        [
            (new(18, 5), new(24, 1), new(28, 6), new(26, 10)),
            (new(26, 10), new(36, 8), new(36, 20), new(28, 24)),
            (new(28, 24), new(32, 34), new(20, 36), new(14, 28)),
            (new(14, 28), new(4, 34), new(1, 20), new(8, 12)),
            (new(8, 12), new(2, 6), new(12, 2), new(18, 5))
        ]
    ];
}
