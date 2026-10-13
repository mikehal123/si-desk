namespace Sovereign.Desktop.Connector;

public enum GrokbotDrawOp { Move, Line, Curve, Ellipse, Eye }

public readonly record struct GrokbotPoint(double X, double Y);

public readonly record struct GrokbotDrawStep(
    GrokbotDrawOp Op,
    double X,
    double Y,
    double Width,
    double Height,
    double ControlX,
    double ControlY);

/// <summary>Flat paint list for one Grokbot mark. The desktop mark draws these steps and no badge.</summary>
public sealed class GrokbotAvatarPaint
{
    public const double TiltDegrees = -8;
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte EyeR { get; }
    public byte EyeG { get; }
    public byte EyeB { get; }
    public string Shape { get; }
    public double Size { get; }
    public double Tilt { get; }
    public bool Badge { get; }
    public double BodyRadius { get; }
    public IReadOnlyList<GrokbotDrawStep> Steps { get; }
    public IReadOnlyList<GrokbotPoint> Hull { get; }

    private GrokbotAvatarPaint(
        byte r, byte g, byte b, string shape, double size, double bodyRadius,
        IReadOnlyList<GrokbotDrawStep> steps, IReadOnlyList<GrokbotPoint> hull)
    {
        R = r; G = g; B = b;
        EyeR = 26; EyeG = 28; EyeB = 36;
        Shape = shape;
        Size = size;
        Tilt = TiltDegrees;
        Badge = false;
        BodyRadius = bodyRadius;
        Steps = steps;
        Hull = hull;
    }

    public static GrokbotAvatarPaint Create(string? color, string? shape, double size)
    {
        if (double.IsNaN(size) || double.IsInfinity(size) || size < 1) size = 1;
        var token = GrokbotAvatarChoice.NormalizeShape(shape);
        var ink = GrokbotAvatarChoice.Ink(color);
        var body = size * 0.82;
        var origin = (size - body) / 2;
        var steps = new List<GrokbotDrawStep>();
        IReadOnlyList<GrokbotPoint> hull;
        double bodyRadius;
        if (token == "round")
        {
            steps.Add(new GrokbotDrawStep(GrokbotDrawOp.Ellipse, origin, origin, body, body, 0, 0));
            hull = [];
            bodyRadius = body / 2;
        }
        else
        {
            var local = token == "hex" ? Hex(body) : Triangle(body);
            var radius = body * (token == "hex" ? 0.16 : 0.22);
            steps.AddRange(Rounded(local, radius, origin));
            hull = local.Select(point => new GrokbotPoint(point.X + origin, point.Y + origin)).ToArray();
            bodyRadius = 0;
        }
        var eyeW = size * 0.08;
        var eyeH = size * 0.15;
        var eyeY = size * 0.32;
        steps.Add(new GrokbotDrawStep(GrokbotDrawOp.Eye, size * 0.40, eyeY, eyeW, eyeH, 0, 0));
        steps.Add(new GrokbotDrawStep(GrokbotDrawOp.Eye, size * 0.52, eyeY, eyeW, eyeH, 0, 0));
        return new GrokbotAvatarPaint(ink.R, ink.G, ink.B, token, size, bodyRadius, steps, hull);
    }

    private static GrokbotPoint[] Triangle(double body) =>
    [
        new(body * 0.50, body * 0.06),
        new(body * 0.06, body * 0.94),
        new(body * 0.94, body * 0.94)
    ];

    private static GrokbotPoint[] Hex(double body)
    {
        var center = body / 2;
        var radius = body * 0.46;
        var points = new GrokbotPoint[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 3;
            points[i] = new GrokbotPoint(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
        }
        return points;
    }

    private static List<GrokbotDrawStep> Rounded(IReadOnlyList<GrokbotPoint> points, double radius, double origin)
    {
        var count = points.Count;
        var starts = new GrokbotPoint[count];
        var ends = new GrokbotPoint[count];
        for (var i = 0; i < count; i++)
        {
            var previous = points[(i + count - 1) % count];
            var current = points[i];
            var next = points[(i + 1) % count];
            var toPrevious = Unit(previous.X - current.X, previous.Y - current.Y);
            var toNext = Unit(next.X - current.X, next.Y - current.Y);
            var cut = Math.Min(radius, 0.45 * Math.Min(Distance(previous, current), Distance(current, next)));
            starts[i] = new GrokbotPoint(current.X + toPrevious.X * cut, current.Y + toPrevious.Y * cut);
            ends[i] = new GrokbotPoint(current.X + toNext.X * cut, current.Y + toNext.Y * cut);
        }
        var steps = new List<GrokbotDrawStep>
        {
            new(GrokbotDrawOp.Move, starts[0].X + origin, starts[0].Y + origin, 0, 0, 0, 0)
        };
        for (var i = 0; i < count; i++)
        {
            var current = points[i];
            steps.Add(new GrokbotDrawStep(GrokbotDrawOp.Curve, ends[i].X + origin, ends[i].Y + origin, 0, 0, current.X + origin, current.Y + origin));
            var next = starts[(i + 1) % count];
            steps.Add(new GrokbotDrawStep(GrokbotDrawOp.Line, next.X + origin, next.Y + origin, 0, 0, 0, 0));
        }
        return steps;
    }

    private static GrokbotPoint Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-9 ? new GrokbotPoint(0, 0) : new GrokbotPoint(x / length, y / length);
    }

    private static double Distance(GrokbotPoint a, GrokbotPoint b)
    {
        var x = a.X - b.X;
        var y = a.Y - b.Y;
        return Math.Sqrt(x * x + y * y);
    }
}
