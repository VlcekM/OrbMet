using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrbMet.Core.Usage;

namespace OrbMet.Desktop.Infrastructure;

/// <summary>
/// A small hex colour picker: a hex box plus hue, saturation and brightness
/// sliders that stay in step, with a swatch. Every change is previewed at once
/// through <c>preview</c>; Cancel or closing restores the colour it opened with.
/// </summary>
internal sealed class ColourPickerWindow : Window
{
    private readonly TextBox _hex = new() { Width = 90, MaxLength = 7, FontFamily = new FontFamily("Consolas") };
    private readonly Slider _hue = new() { Minimum = 0, Maximum = 359.999 };
    private readonly Slider _saturation = new() { Minimum = 0, Maximum = 1 };
    private readonly Slider _value = new() { Minimum = 0, Maximum = 1 };
    private readonly Border _swatch = new() { Width = 40, Height = 24, CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Color _original;
    private readonly Action<Color> _preview;
    private bool _syncing;
    private bool _accepted;

    private ColourPickerWindow(string title, Color start, Action<Color> preview)
    {
        _original = start;
        _preview = preview;
        Title = title;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        Foreground = Brushes.White;

        var text = Localizer.Instance.Text;
        var ok = new Button { Content = text["ColourOk"], Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = text["ColourCancel"], Width = 80, IsCancel = true };
        ok.Click += (_, _) => { _accepted = true; Close(); };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(_swatch);
        top.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) });
        top.Children.Add(_hex);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(top);
        panel.Children.Add(Row(text["ColourHue"], _hue));
        panel.Children.Add(Row(text["ColourSaturation"], _saturation));
        panel.Children.Add(Row(text["ColourBrightness"], _value));
        panel.Children.Add(buttons);
        Content = panel;

        _hex.TextChanged += (_, _) =>
        {
            if (!_syncing && HexColour.TryParse(_hex.Text, out var r, out var g, out var b))
            {
                Set(Color.FromRgb(r, g, b), fromHex: true);
            }
        };
        _hue.ValueChanged += (_, _) => FromSliders();
        _saturation.ValueChanged += (_, _) => FromSliders();
        _value.ValueChanged += (_, _) => FromSliders();
        Closed += (_, _) =>
        {
            if (!_accepted)
            {
                _preview(_original);
            }
        };

        Set(start, fromHex: false);
    }

    private Color Chosen { get; set; }

    /// <summary>Shows the picker modally; returns the chosen "#RRGGBB", or null if cancelled.</summary>
    public static string? Pick(string title, Color start, Action<Color> preview)
    {
        var window = new ColourPickerWindow(title, start, preview);
        window.ShowDialog();
        return window._accepted ? HexColour.Format(window.Chosen.R, window.Chosen.G, window.Chosen.B) : null;
    }

    private static UIElement Row(string label, Slider slider)
    {
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        row.Children.Add(slider);
        return row;
    }

    private void FromSliders()
    {
        if (!_syncing)
        {
            Set(FromHsv(_hue.Value, _saturation.Value, _value.Value), fromHex: false, fromSliders: true);
        }
    }

    private void Set(Color colour, bool fromHex, bool fromSliders = false)
    {
        _syncing = true;
        Chosen = colour;
        _swatch.Background = new SolidColorBrush(colour);
        if (!fromHex)
        {
            _hex.Text = HexColour.Format(colour.R, colour.G, colour.B).TrimStart('#');
        }

        if (!fromSliders)
        {
            var (h, s, v) = ToHsv(colour);
            // A grey has no hue of its own; keep the slider where the user left it.
            if (s > 0 && v > 0)
            {
                _hue.Value = h;
            }

            _saturation.Value = s;
            _value.Value = v;
        }

        _syncing = false;
        _preview(colour);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        var h = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta % 6 + 6) % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return (h, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
