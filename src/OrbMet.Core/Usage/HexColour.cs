using System.Globalization;

namespace OrbMet.Core.Usage;

/// <summary>A colour typed as hex: #RGB or #RRGGBB, the # optional. Anything else is not a colour.</summary>
public static class HexColour
{
    public static bool TryParse(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var digits = text?.Trim().TrimStart('#') ?? string.Empty;
        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(c => $"{c}{c}"));
        }

        if (digits.Length != 6 || !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }

    /// <summary>The canonical "#RRGGBB" for a valid colour, otherwise null.</summary>
    public static string? Normalize(string? text) =>
        TryParse(text, out var r, out var g, out var b) ? Format(r, g, b) : null;

    public static string Format(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
