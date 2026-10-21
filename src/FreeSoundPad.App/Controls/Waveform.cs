using System.Windows;
using System.Windows.Media;

namespace FreeSoundPad.App.Controls;

/// <summary>Renders a compact waveform from normalized peaks.</summary>
public sealed class Waveform : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(IReadOnlyList<float>), typeof(Waveform),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(Waveform),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarRadiusProperty = DependencyProperty.Register(
        nameof(BarRadius), typeof(double), typeof(Waveform),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float>? Peaks
    {
        get => (IReadOnlyList<float>?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double BarRadius
    {
        get => (double)GetValue(BarRadiusProperty);
        set => SetValue(BarRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var peaks = Peaks;
        if (peaks == null || peaks.Count == 0) return;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var gap = Math.Max(0, Gap);
        var barWidth = 3.0;
        var bars = Math.Max(1, (int)((width + gap) / (barWidth + gap)));
        bars = Math.Min(bars, peaks.Count);

        var totalWidth = bars * barWidth + (bars - 1) * gap;
        var startX = Math.Max(0, (width - totalWidth) / 2);
        var brush = BarBrush ?? Brushes.White;

        for (var i = 0; i < bars; i++)
        {
            var index = (int)((long)i * peaks.Count / bars);
            var value = Math.Clamp(peaks[index], 0.02f, 1f);
            var barHeight = Math.Max(2, value * height);
            var x = startX + i * (barWidth + gap);
            var y = height - barHeight;
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), BarRadius, BarRadius);
        }
    }
}
