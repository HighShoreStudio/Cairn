using System.Windows;
using System.Windows.Media;

namespace HighshoreCairn.Helpers;

/// <summary>
/// Small circle that fills up clockwise like a pie as <see cref="Value"/> goes from 0 to 1.
/// At 100% it becomes a full disc with a check mark.
/// XAML: &lt;h:ProgressRing Width="16" Height="16" Value="{Binding Progress}" ... /&gt;
/// </summary>
public class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompleteBrushProperty = DependencyProperty.Register(
        nameof(CompleteBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Completion from 0 (empty) to 1 (full).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush CompleteBrush
    {
        get => (Brush)GetValue(CompleteBrushProperty);
        set => SetValue(CompleteBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 1;
        var value = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);

        if (value >= 0.999)
        {
            dc.DrawEllipse(CompleteBrush, null, center, radius, radius);
            var pen = new Pen(Brushes.White, Math.Max(1.2, size / 10))
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            var a = new Point(center.X - radius * 0.45, center.Y + radius * 0.02);
            var b = new Point(center.X - radius * 0.12, center.Y + radius * 0.36);
            var c = new Point(center.X + radius * 0.48, center.Y - radius * 0.32);
            dc.DrawLine(pen, a, b);
            dc.DrawLine(pen, b, c);
            return;
        }

        dc.DrawEllipse(null, new Pen(TrackBrush, 1.5), center, radius, radius);
        if (value <= 0.001) return;

        // Pie slice from 12 o'clock, clockwise.
        var inner = Math.Max(1, radius - 2);
        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - inner);
        var end = new Point(center.X + inner * Math.Sin(angle), center.Y - inner * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(center, isFilled: true, isClosed: true);
            ctx.LineTo(start, false, false);
            ctx.ArcTo(end, new Size(inner, inner), 0, value > 0.5, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(ProgressBrush, null, geometry);
    }
}
