using System.Windows;
using System.Windows.Media;

namespace FreeSoundPad.App.Controls;

/// <summary>Segment VU meter with a green → amber → red palette.</summary>
public sealed class VuMeter : FrameworkElement
{
    private static readonly Brush[] Palette = BuildPalette();
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VuMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(VuMeter),
        new FrameworkPropertyMetadata(18, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical), typeof(bool), typeof(VuMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(VuMeter),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var segments = Math.Max(2, Segments);
        var gap = Math.Max(0, Gap);
        var level = Math.Clamp(Level / 100.0, 0, 1);
        var lit = (int)Math.Round(level * segments);

        if (IsVertical)
        {
            var segmentHeight = Math.Max(1, (height - gap * (segments - 1)) / segments);
            for (var i = 0; i < segments; i++)
            {
                var y = height - (i + 1) * segmentHeight - i * gap;
                var brush = i < lit ? Palette[Math.Min(i, Palette.Length - 1)] : DimBrush;
                drawingContext.DrawRoundedRectangle(brush, null, new Rect(0, y, width, segmentHeight), 1, 1);
            }
        }
        else
        {
            var segmentWidth = Math.Max(1, (width - gap * (segments - 1)) / segments);
            for (var i = 0; i < segments; i++)
            {
                var x = i * (segmentWidth + gap);
                var brush = i < lit ? Palette[Math.Min(i, Palette.Length - 1)] : DimBrush;
                drawingContext.DrawRoundedRectangle(brush, null, new Rect(x, 0, segmentWidth, height), 1, 1);
            }
        }
    }

    private static Brush[] BuildPalette()
    {
        const int count = 24;
        var brushes = new Brush[count];
        for (var i = 0; i < count; i++)
        {
            var hue = 152 - i / (double)(count - 1) * 122;
            brushes[i] = Freeze(new SolidColorBrush(FromHsl(hue, 0.78, 0.56)));
        }

        return brushes;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;

        double r = 0, g = 0, b = 0;
        switch (hue)
        {
            case < 60:
                r = c;
                g = x;
                break;
            case < 120:
                r = x;
                g = c;
                break;
            case < 180:
                g = c;
                b = x;
                break;
            case < 240:
                g = x;
                b = c;
                break;
            case < 300:
                r = x;
                b = c;
                break;
            default:
                r = c;
                b = x;
                break;
        }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
