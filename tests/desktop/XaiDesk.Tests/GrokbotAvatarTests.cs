using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokbotAvatarTests
{
    [Fact]
    public void Old_state_without_avatar_fields_loads_blue_round()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-avatar-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "state.json"), "{\"tabEnabled\":false,\"allowControl\":true}");
            var mail = GrokbotMailbox.Open(folder);
            var prefs = mail.PreferencesCopy();
            Assert.Equal("blue", prefs.AvatarColor);
            Assert.Equal("round", prefs.AvatarShape);
            Assert.False(prefs.TabEnabled);
            Assert.True(prefs.AllowControl);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Pink_hex_persists_and_keeps_the_other_state_fields()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-avatar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = GrokbotMailbox.Open(folder);
            var stamp = DateTimeOffset.Parse("2026-10-12T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            first.NotePoll(stamp);
            var prefs = first.PreferencesCopy();
            prefs.TabEnabled = false;
            prefs.AllowControl = true;
            prefs.Screenshots = true;
            prefs.Stopped = true;
            prefs.WakeUrl = "https://wake.example/hook";
            prefs.AvatarColor = "pink";
            prefs.AvatarShape = "hex";
            first.SavePreferences(prefs);
            var state = File.ReadAllText(Path.Combine(folder, "state.json"));
            Assert.Contains("avatarColor", state, StringComparison.Ordinal);
            Assert.Contains("avatarShape", state, StringComparison.Ordinal);
            Assert.Contains("pink", state, StringComparison.Ordinal);
            Assert.Contains("hex", state, StringComparison.Ordinal);
            Assert.Contains("tabEnabled", state, StringComparison.Ordinal);
            Assert.Contains("allowControl", state, StringComparison.Ordinal);
            Assert.Contains("screenshots", state, StringComparison.Ordinal);
            Assert.Contains("stopped", state, StringComparison.Ordinal);
            Assert.Contains("wakeUrl", state, StringComparison.Ordinal);
            Assert.Contains("lastPoll", state, StringComparison.Ordinal);

            var second = GrokbotMailbox.Open(folder);
            var loaded = second.PreferencesCopy();
            Assert.Equal("pink", loaded.AvatarColor);
            Assert.Equal("hex", loaded.AvatarShape);
            Assert.False(loaded.TabEnabled);
            Assert.True(loaded.AllowControl);
            Assert.True(loaded.Screenshots);
            Assert.True(loaded.Stopped);
            Assert.Equal("https://wake.example/hook", loaded.WakeUrl);
            Assert.Equal(stamp, second.Link(false).LastPoll);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Mixed_case_avatar_tokens_store_lowercase()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-avatar-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mail = GrokbotMailbox.Open(folder);
            var prefs = mail.PreferencesCopy();
            prefs.AvatarColor = "PINK";
            prefs.AvatarShape = "Hex";
            mail.SavePreferences(prefs);
            var stored = mail.PreferencesCopy();
            Assert.Equal("pink", stored.AvatarColor);
            Assert.Equal("hex", stored.AvatarShape);
            var state = File.ReadAllText(Path.Combine(folder, "state.json"));
            Assert.DoesNotContain("PINK", state, StringComparison.Ordinal);
            Assert.DoesNotContain("Hex", state, StringComparison.Ordinal);
            var again = GrokbotMailbox.Open(folder);
            Assert.Equal("pink", again.PreferencesCopy().AvatarColor);
            Assert.Equal("hex", again.PreferencesCopy().AvatarShape);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Unknown_avatar_tokens_store_blue_round()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xai-desk-avatar-bad-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mail = GrokbotMailbox.Open(folder);
            var prefs = mail.PreferencesCopy();
            prefs.AvatarColor = "orange";
            prefs.AvatarShape = "square";
            mail.SavePreferences(prefs);
            var stored = mail.PreferencesCopy();
            Assert.Equal("blue", stored.AvatarColor);
            Assert.Equal("round", stored.AvatarShape);
            var state = File.ReadAllText(Path.Combine(folder, "state.json"));
            Assert.DoesNotContain("orange", state, StringComparison.Ordinal);
            Assert.DoesNotContain("square", state, StringComparison.Ordinal);
            var again = GrokbotMailbox.Open(folder);
            Assert.Equal("blue", again.PreferencesCopy().AvatarColor);
            Assert.Equal("round", again.PreferencesCopy().AvatarShape);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    public static IEnumerable<object[]> EveryColorShapeAndSize()
    {
        foreach (var color in GrokbotAvatarChoice.Colors)
            foreach (var shape in GrokbotAvatarChoice.Shapes)
                foreach (var size in new double[] { 16, 48 })
                    yield return [color, shape, size];
    }

    [Theory]
    [MemberData(nameof(EveryColorShapeAndSize))]
    public void Every_color_and_shape_renders_two_eyes(string color, string shape, double size)
    {
        var paint = GrokbotAvatarPaint.Create(color, shape, size);
        var ink = GrokbotAvatarChoice.Ink(color);
        var eyes = paint.Steps.Where(step => step.Op == GrokbotDrawOp.Eye).ToArray();

        Assert.Equal(ink.R, paint.R);
        Assert.Equal(ink.G, paint.G);
        Assert.Equal(ink.B, paint.B);
        Assert.Equal(shape, paint.Shape);
        Assert.False(paint.Badge);
        Assert.Equal(GrokbotAvatarPaint.TiltDegrees, paint.Tilt);
        Assert.InRange(paint.Tilt, -14, -4);
        Assert.True(paint.EyeR < 48 && paint.EyeG < 48 && paint.EyeB < 48);
        Assert.Equal(2, eyes.Length);
        Assert.DoesNotContain(paint.Steps, step => step.Op is not (GrokbotDrawOp.Move or GrokbotDrawOp.Line or GrokbotDrawOp.Curve or GrokbotDrawOp.Ellipse or GrokbotDrawOp.Eye));

        if (shape == "round")
        {
            Assert.Contains(paint.Steps, step => step.Op == GrokbotDrawOp.Ellipse);
            Assert.DoesNotContain(paint.Steps, step => step.Op == GrokbotDrawOp.Curve);
            Assert.True(paint.BodyRadius > size * 0.3);
        }
        else
        {
            var curves = paint.Steps.Count(step => step.Op == GrokbotDrawOp.Curve);
            Assert.Equal(shape == "hex" ? 6 : 3, curves);
            Assert.Equal(shape == "hex" ? 6 : 3, paint.Hull.Count);
            Assert.DoesNotContain(paint.Steps, step => step.Op == GrokbotDrawOp.Ellipse);
        }

        foreach (var eye in eyes)
        {
            Assert.True(eye.Height > eye.Width * 1.5);
            Assert.True(eye.Width >= 1);
            Assert.InRange(eye.X, 0, size);
            Assert.InRange(eye.Y, 0, size);
            Assert.True(eye.X + eye.Width <= size);
            Assert.True(eye.Y + eye.Height <= size);
            var centerY = eye.Y + eye.Height / 2;
            var centerX = eye.X + eye.Width / 2;
            Assert.True(centerY < size * 0.5);
            Assert.InRange(centerX, size * 0.25, size * 0.75);
            Assert.True(Inside(paint, eye.X, eye.Y));
            Assert.True(Inside(paint, eye.X + eye.Width, eye.Y));
            Assert.True(Inside(paint, eye.X, eye.Y + eye.Height));
            Assert.True(Inside(paint, eye.X + eye.Width, eye.Y + eye.Height));
        }
    }

    [Fact]
    public void Eyes_scale_from_16_to_48()
    {
        var small = GrokbotAvatarPaint.Create("teal", "triangle", 16);
        var large = GrokbotAvatarPaint.Create("teal", "triangle", 48);
        Assert.Equal(small.Steps.Count, large.Steps.Count);
        for (var i = 0; i < small.Steps.Count; i++)
        {
            Assert.Equal(small.Steps[i].Op, large.Steps[i].Op);
            Assert.Equal(small.Steps[i].X * 3, large.Steps[i].X, 6);
            Assert.Equal(small.Steps[i].Y * 3, large.Steps[i].Y, 6);
            Assert.Equal(small.Steps[i].Width * 3, large.Steps[i].Width, 6);
            Assert.Equal(small.Steps[i].Height * 3, large.Steps[i].Height, 6);
        }
    }

    [Fact]
    public void Unknown_tokens_still_render_blue_round_eyes()
    {
        var paint = GrokbotAvatarPaint.Create("orange", "square", 32);
        Assert.Equal("round", paint.Shape);
        Assert.Equal(GrokbotAvatarChoice.Ink("blue"), (paint.R, paint.G, paint.B));
        Assert.False(paint.Badge);
        Assert.Equal(2, paint.Steps.Count(step => step.Op == GrokbotDrawOp.Eye));
    }

    private static bool Inside(GrokbotAvatarPaint paint, double x, double y)
    {
        if (paint.BodyRadius > 0)
        {
            var dx = x - paint.Size / 2;
            var dy = y - paint.Size / 2;
            return dx * dx + dy * dy <= paint.BodyRadius * paint.BodyRadius + 1e-6;
        }
        return paint.Hull.Count == 3 ? InTriangle(x, y, paint.Hull[0], paint.Hull[1], paint.Hull[2]) : InPolygon(x, y, paint.Hull);
    }

    private static bool InTriangle(double x, double y, GrokbotPoint a, GrokbotPoint b, GrokbotPoint c)
    {
        var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        var wa = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / denominator;
        var wb = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / denominator;
        var wc = 1 - wa - wb;
        return wa >= -0.02 && wb >= -0.02 && wc >= -0.02;
    }

    private static bool InPolygon(double x, double y, IReadOnlyList<GrokbotPoint> hull)
    {
        var inside = false;
        for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
        {
            var cross = (hull[i].Y > y) != (hull[j].Y > y)
                && x < (hull[j].X - hull[i].X) * (y - hull[i].Y) / (hull[j].Y - hull[i].Y) + hull[i].X;
            if (cross) inside = !inside;
        }
        return inside;
    }
}
