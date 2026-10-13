namespace Sovereign.Desktop.Connector;

public sealed record GrokbotControl(string Id, string Type, string Label, int X, int Y, int Width, int Height, bool Password = false);

public readonly record struct GrokbotWindowBounds(int X, int Y, int Width, int Height);

public readonly record struct GrokbotPng(string Text, byte[]? Bytes);

/// <summary>Window-only checks for Grokbot. Coordinates outside the SI Desk window are refused, and secret fields are blocked.</summary>
public static class GrokbotWindow
{
    private static readonly string[] BlockedIds =
    [
        "grokbot-blocked-token",
        "grokbot-blocked-xai-key",
        "xai api key",
        "grok bot connector token"
    ];

    public static bool ContainsPoint(GrokbotWindowBounds window, int x, int y) =>
        window.Width > 0 && window.Height > 0 &&
        x >= window.X && y >= window.Y && x < window.X + window.Width && y < window.Y + window.Height;

    public static bool IsSecret(string? id, string? type, string? label, bool password = false)
    {
        if (password) return true;
        if (Has(type, "password")) return true;
        if (Listed(id) || Listed(label)) return true;
        if (Has(id, "api key") || Has(label, "api key")) return true;
        if (Has(id, "api-key") || Has(label, "api-key")) return true;
        if (Has(id, "api_key") || Has(label, "api_key")) return true;
        if (Has(id, "token") || Has(label, "token")) return true;
        if (Has(id, "password") || Has(label, "password")) return true;
        if (Has(id, "secret") || Has(label, "secret")) return true;
        return false;
    }

    public static void RequireClick(GrokbotWindowBounds window, GrokbotControl? control, int? x, int? y, GrokbotControl? hit)
    {
        if (control is not null)
        {
            if (IsSecret(control.Id, control.Type, control.Label, control.Password))
                throw new InvalidOperationException("That control is blocked.");
            RequireInside(window, control);
            return;
        }
        if (x is int px && y is int py)
        {
            if (!ContainsPoint(window, px, py))
                throw new InvalidOperationException("Those coordinates are outside the SI Desk window.");
            if (hit is not null && IsSecret(hit.Id, hit.Type, hit.Label, hit.Password))
                throw new InvalidOperationException("That control is blocked.");
            return;
        }
        throw new ArgumentException("Provide a control id or coordinates inside the window.");
    }

    public static void RequireType(string controlId, string? type, string? label, string text, bool password = false)
    {
        if (string.IsNullOrWhiteSpace(controlId)) throw new ArgumentException("Provide a control id.");
        if (IsSecret(controlId, type, label, password)) throw new InvalidOperationException("That control is blocked.");
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Text is empty.");
        if (text.Length > 8000) throw new ArgumentException("Text is too long.");
        foreach (var ch in text)
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')
                throw new ArgumentException("Text has unsupported characters.");
    }

    public static void RequireInside(GrokbotWindowBounds window, GrokbotControl control)
    {
        var cx = control.X + control.Width / 2;
        var cy = control.Y + control.Height / 2;
        if (!ContainsPoint(window, cx, cy))
            throw new InvalidOperationException("That control is outside the SI Desk window.");
    }

    public static void RequireFocused(bool insideWindow, bool secret)
    {
        if (!insideWindow) throw new InvalidOperationException("Focus is outside the SI Desk window.");
        if (secret) throw new InvalidOperationException("That control is blocked.");
    }

    public static GrokbotControl? SmallestHit(IEnumerable<GrokbotControl> controls, int x, int y)
    {
        GrokbotControl? best = null;
        var area = int.MaxValue;
        foreach (var control in controls)
        {
            if (!ContainsPoint(new GrokbotWindowBounds(control.X, control.Y, control.Width, control.Height), x, y)) continue;
            var size = Math.Max(1, control.Width) * Math.Max(1, control.Height);
            if (size < area)
            {
                area = size;
                best = control;
            }
        }
        return best;
    }

    public static void RequireSafeKeys(string keys)
    {
        var parsed = ParseKeys(keys);
        var alt = parsed.Modifiers.Contains((ushort)0x12);
        var ctrl = parsed.Modifiers.Contains((ushort)0x11);
        if ((alt && parsed.Key is 0x09 or 0x1B) || (ctrl && parsed.Key == 0x1B))
            throw new InvalidOperationException("That key would leave the SI Desk window.");
    }

    public static (ushort[] Modifiers, ushort Key) ParseKeys(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) throw new ArgumentException("Keys are empty.");
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) throw new ArgumentException("Keys are not recognized.");
        var modifiers = new List<ushort>();
        ushort? key = null;
        foreach (var part in parts)
        {
            if (Modifier(part, out var mod))
            {
                modifiers.Add(mod);
                continue;
            }
            if (key is not null) throw new ArgumentException("Keys are not recognized.");
            key = KeyCode(part);
        }
        if (key is null) throw new ArgumentException("Keys are not recognized.");
        return (modifiers.ToArray(), key.Value);
    }

    public static string CanonicalPage(string? page)
    {
        var name = (page ?? "").Trim();
        return name.ToLowerInvariant() switch
        {
            "chat" => "chat",
            "shared room" or "room" => "room",
            "settings" => "settings",
            "grokbot" => "grokbot",
            _ => throw new ArgumentException("Page must be Chat, Shared room, Settings, or Grokbot.")
        };
    }

    private static bool Listed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        foreach (var id in BlockedIds)
            if (text.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Has(string? value, string phrase) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static bool Modifier(string part, out ushort code)
    {
        code = part.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            _ => 0
        };
        return code != 0;
    }

    private static ushort KeyCode(string part)
    {
        var name = part.ToLowerInvariant();
        var named = name switch
        {
            "enter" or "return" => (ushort)0x0D,
            "escape" or "esc" => (ushort)0x1B,
            "tab" => (ushort)0x09,
            "backspace" => (ushort)0x08,
            "delete" or "del" => (ushort)0x2E,
            "space" => (ushort)0x20,
            "left" => (ushort)0x25,
            "up" => (ushort)0x26,
            "right" => (ushort)0x27,
            "down" => (ushort)0x28,
            _ => (ushort)0
        };
        if (named != 0) return named;
        if (part.Length == 1)
        {
            var ch = char.ToUpperInvariant(part[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return (ushort)ch;
        }
        throw new ArgumentException("Keys are not recognized.");
    }
}
