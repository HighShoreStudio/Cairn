using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HighshoreCairn.Converters;
using HighshoreCairn.Helpers;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Draws the roadmap: the timeline at the top, one row per tag (plus the Milestones and the Custom rows)
/// with their bars, and handles the mouse and the keyboard. What is shown comes from the
/// <see cref="RoadmapViewModel"/> (rows, period on screen, selection); this control only paints it.
///
///   click = select a bar · double click = open it · right click = menu · Del = delete (milestones, custom)
///   row header: eye = close / open the row, drag = reorder the tag rows
///   free view: wheel = zoom, middle button drag = move, arrows / Shift+arrows = move slowly / fast
/// </summary>
public class RoadmapView : FrameworkElement
{
    private const double HeaderWidth = 176;     // column with the names of the rows
    private const double TimelineHeight = 46;   // months + days / weeks
    private const double LaneHeight = 30;
    private const double BarHeight = 22;
    private const double RowGap = 8;
    private const double CollapsedHeight = 26;
    private const double EyeSize = 26;

    private RoadmapViewModel? _vm;
    private double _scrollY;
    private double _contentHeight;

    // What is on screen, rebuilt at every paint: used to find what is under the mouse.
    private readonly List<(Rect Rect, RoadmapBar Bar)> _barRects = new();
    private readonly List<(Rect Header, Rect Eye, Rect Area, RoadmapRow Row)> _rowRects = new();

    // Mouse state
    private bool _panning;
    private Point _panStart;
    private DateTime _panFrom;
    private double _panScroll;
    private RoadmapRow? _dragRow;       // a tag row being dragged by its header
    private Point _dragStart;
    private bool _dragging;
    private int _dropIndex = -1;
    private RoadmapBar? _hoverBar;

    public RoadmapView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        DataContextChanged += (_, _) => Attach(DataContext as RoadmapViewModel);
        Loaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnChanged;
            ThemeService.ThemeChanged += OnChanged;
            Attach(DataContext as RoadmapViewModel);
        };
        Unloaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnChanged;
            Attach(null);
        };
    }

    private void Attach(RoadmapViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null) _vm.Changed -= OnChanged;
        _vm = vm;
        if (_vm != null) _vm.Changed += OnChanged;
        InvalidateVisual();
    }

    private void OnChanged() => InvalidateVisual();

    // ============================================================================ geometry

    private double PlotWidth => Math.Max(1, ActualWidth - HeaderWidth);

    private double XOf(DateTime moment) =>
        _vm is null ? 0 : HeaderWidth + (moment - _vm.ViewFrom).TotalDays / _vm.ViewDays * PlotWidth;

    private DateTime DateAt(double x) =>
        _vm is null ? DateTime.Today : _vm.ViewFrom.AddDays((x - HeaderWidth) / PlotWidth * _vm.ViewDays);

    private static double RowHeight(RoadmapRow row) =>
        row.Collapsed ? CollapsedHeight : Math.Max(1, row.Lanes.Count) * LaneHeight + 6;

    private void ClampScroll()
    {
        var max = Math.Max(0, _contentHeight - (ActualHeight - TimelineHeight));
        _scrollY = Math.Clamp(_scrollY, 0, max);
    }

    // ============================================================================ colors

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Color ColorOf(Brush brush, Color fallback) => brush is SolidColorBrush solid ? solid.Color : fallback;

    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R * amount + b.R * (1 - amount)),
        (byte)Math.Round(a.G * amount + b.G * (1 - amount)),
        (byte)Math.Round(a.B * amount + b.B * (1 - amount)));

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Diagonal stripes of two colors (a transparent second color gives "hatched" stripes).</summary>
    private static Brush Stripes(Color first, Color second)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(7, 7),
            SpreadMethod = GradientSpreadMethod.Repeat
        };
        brush.GradientStops.Add(new GradientStop(first, 0));
        brush.GradientStops.Add(new GradientStop(first, 0.5));
        brush.GradientStops.Add(new GradientStop(second, 0.5));
        brush.GradientStops.Add(new GradientStop(second, 1));
        brush.Freeze();
        return brush;
    }

    // ============================================================================ painting

    private FormattedText Text(string text, double size, Brush brush, FontWeight weight, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        if (maxWidth > 0) formatted.MaxTextWidth = maxWidth;
        return formatted;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var surface = Find("Brush.Surface", Brushes.White);
        var canvas = Find("Brush.Canvas", Brushes.White);
        dc.DrawRectangle(canvas, null, new Rect(0, 0, ActualWidth, ActualHeight));
        _barRects.Clear();
        _rowRects.Clear();
        var vm = _vm;
        if (vm is null || ActualWidth <= HeaderWidth + 20 || ActualHeight <= TimelineHeight + 10) return;

        var border = Find("Brush.Border", Brushes.LightGray);
        var grid = Find("Brush.Grid", Brushes.Gainsboro);
        var text = Find("Brush.Text", Brushes.Black);
        var muted = Find("Brush.TextMuted", Brushes.Gray);
        var chip = Find("Brush.Chip", Brushes.WhiteSmoke);
        var primary = Find("Brush.Primary", Brushes.SteelBlue);
        var accent = Find("Brush.Accent", Brushes.Orange);
        var surfaceColor = ColorOf(surface, Colors.White);
        var borderPen = new Pen(border, 1);
        borderPen.Freeze();

        // ---------------------------------------------------------------- rows: heights first
        _contentHeight = vm.Rows.Sum(r => RowHeight(r) + RowGap) + 8;
        ClampScroll();

        // ---------------------------------------------------------------- vertical lines of the timeline
        DrawTimelineLines(dc, vm, grid, border);

        // ---------------------------------------------------------------- rows
        dc.PushClip(new RectangleGeometry(new Rect(0, TimelineHeight, ActualWidth, ActualHeight - TimelineHeight)));
        var y = TimelineHeight + 8 - _scrollY;
        var (selectionBack, selectionText) = vm.SelectionColors;
        var selectedBrush = HexToBrushConverter.Parse(selectionBack);
        var selectedText = HexToBrushConverter.Parse(selectionText);

        foreach (var row in vm.Rows)
        {
            var height = RowHeight(row);
            var area = new Rect(HeaderWidth, y, PlotWidth, height);
            var visible = y + height >= TimelineHeight && y <= ActualHeight;

            if (visible && !row.Collapsed)
            {
                // A soft band behind every lane, like lines on ruled paper.
                dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, PlotWidth, ActualHeight)));
                var lanes = Math.Max(1, row.Lanes.Count);
                for (var lane = 0; lane < lanes; lane++)
                {
                    var bandTop = y + 3 + lane * LaneHeight;
                    dc.PushOpacity(0.55);
                    dc.DrawRoundedRectangle(chip, null, new Rect(HeaderWidth + 4, bandTop + 2, PlotWidth - 8, LaneHeight - 4), 3, 3);
                    dc.Pop();
                }

                for (var lane = 0; lane < row.Lanes.Count; lane++)
                {
                    var top = y + 3 + lane * LaneHeight + (LaneHeight - BarHeight) / 2;
                    foreach (var bar in row.Lanes[lane])
                        DrawBar(dc, vm, bar, top, surfaceColor, text, selectedBrush, selectedText);
                }
                dc.Pop();
            }

            // Header of the row: a block in the color of the tag, with the eye and the name.
            var header = new Rect(6, y, HeaderWidth - 14, height);
            var eye = new Rect(header.X + 2, header.Y + (Math.Min(height, CollapsedHeight) - EyeSize) / 2, EyeSize, EyeSize);
            _rowRects.Add((header, eye, area, row));
            if (visible)
            {
                var rowColor = HexToBrushConverter.Parse(row.Color);
                var onRow = Luminance(rowColor.Color) > 0.62 ? Brushes.Black : Brushes.White;
                dc.PushOpacity(row.Collapsed ? 0.7 : 1);
                dc.DrawRoundedRectangle(rowColor, null, header, 5, 5);
                dc.Pop();
                DrawEye(dc, eye, onRow, !row.Collapsed);
                var title = Text(row.Title.ToUpperInvariant() + (row.Collapsed && row.Count > 0 ? $"  ({row.Count})" : ""),
                    11.5, onRow, FontWeights.Bold, header.Width - EyeSize - 12);
                dc.DrawText(title, new Point(eye.Right + 4, header.Y + (Math.Min(height, CollapsedHeight) - title.Height) / 2));
            }

            y += height + RowGap;
        }

        // Where a dragged tag row would land.
        if (_dragging && _dropIndex >= 0)
        {
            var tagRows = _rowRects.Where(r => r.Row.Kind == RoadmapRowKind.Tag).ToList();
            if (tagRows.Count > 0)
            {
                var lineY = _dropIndex >= tagRows.Count ? tagRows[^1].Header.Bottom + RowGap / 2 : tagRows[_dropIndex].Header.Top - RowGap / 2;
                dc.DrawRectangle(primary, null, new Rect(4, lineY - 1.5, ActualWidth - 8, 3));
            }
        }
        dc.Pop();

        if (vm.Rows.Count == 0 || vm.Rows.All(r => r.Count == 0))
        {
            var hint = Text("Nothing on the roadmap yet. Give a task a start date plus an end or finish date, or add a milestone.",
                13, muted, FontWeights.Normal, PlotWidth - 40);
            dc.DrawText(hint, new Point(HeaderWidth + (PlotWidth - hint.Width) / 2, Math.Min(ActualHeight - 40, Math.Max(TimelineHeight + 60, y + 20))));
        }

        // ---------------------------------------------------------------- timeline header (stays on top)
        dc.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, TimelineHeight));
        DrawTimelineHeader(dc, vm, text, muted, border, accent);
        dc.DrawLine(borderPen, new Point(0, TimelineHeight + 0.5), new Point(ActualWidth, TimelineHeight + 0.5));

        // Today: a line through the whole chart.
        var todayX = XOf(vm.Now);
        if (todayX >= HeaderWidth && todayX <= ActualWidth)
        {
            var todayPen = new Pen(accent, 1.6);
            todayPen.Freeze();
            dc.DrawLine(todayPen, new Point(todayX, TimelineHeight - 12), new Point(todayX, ActualHeight));
            dc.DrawEllipse(accent, null, new Point(todayX, TimelineHeight - 12), 3.5, 3.5);
        }

        // A thin indicator when there are more rows than fit.
        var viewport = ActualHeight - TimelineHeight;
        if (_contentHeight > viewport + 1)
        {
            var thumbHeight = Math.Max(24, viewport * viewport / _contentHeight);
            var thumbTop = TimelineHeight + (_scrollY / (_contentHeight - viewport)) * (viewport - thumbHeight);
            dc.DrawRoundedRectangle(Find("Brush.Scroll", Brushes.Silver), null, new Rect(ActualWidth - 6, thumbTop, 4, thumbHeight), 2, 2);
        }
    }

    /// <summary>The eye of a row header: open = the row is shown, crossed out = closed.</summary>
    private static void DrawEye(DrawingContext dc, Rect box, Brush brush, bool open)
    {
        var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var pen = new Pen(brush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(new Point(center.X - 7.5, center.Y), false, true);
            context.QuadraticBezierTo(new Point(center.X, center.Y - 7), new Point(center.X + 7.5, center.Y), true, true);
            context.QuadraticBezierTo(new Point(center.X, center.Y + 7), new Point(center.X - 7.5, center.Y), true, true);
        }
        outline.Freeze();
        dc.DrawGeometry(null, pen, outline);
        if (open) dc.DrawEllipse(brush, null, center, 2.3, 2.3);
        else dc.DrawLine(pen, new Point(center.X - 6.5, center.Y + 5.5), new Point(center.X + 6.5, center.Y - 5.5));
    }

    private void DrawTimelineLines(DrawingContext dc, RoadmapViewModel vm, Brush grid, Brush border)
    {
        var monthPen = new Pen(border, 1);
        monthPen.Freeze();
        var weekPen = new Pen(grid, 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        weekPen.Freeze();
        var pixelsPerDay = PlotWidth / vm.ViewDays;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, PlotWidth, ActualHeight)));
        var first = new DateTime(vm.ViewFrom.Year, vm.ViewFrom.Month, 1);
        for (var month = first; month <= vm.ViewTo; month = month.AddMonths(1))
        {
            var x = Math.Round(XOf(month)) + 0.5;
            if (x >= HeaderWidth) dc.DrawLine(monthPen, new Point(x, TimelineHeight - 24), new Point(x, ActualHeight));
        }

        // Weeks (Mondays) when there is room for them; days when zoomed in a lot.
        if (pixelsPerDay >= 2.2)
        {
            var day = vm.ViewFrom.Date;
            var step = pixelsPerDay >= 16 ? 1 : 7;
            if (step == 7) while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
            for (; day <= vm.ViewTo; day = day.AddDays(step))
            {
                if (day.Day == 1) continue;
                var x = Math.Round(XOf(day)) + 0.5;
                dc.DrawLine(weekPen, new Point(x, TimelineHeight), new Point(x, ActualHeight));
            }
        }
        dc.Pop();
    }

    private void DrawTimelineHeader(DrawingContext dc, RoadmapViewModel vm, Brush text, Brush muted, Brush border, Brush accent)
    {
        var inv = CultureInfo.InvariantCulture;
        var pixelsPerDay = PlotWidth / vm.ViewDays;
        var monthPen = new Pen(border, 1);
        monthPen.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, PlotWidth, TimelineHeight)));

        // Months (with the year on January and on the first month shown).
        var first = new DateTime(vm.ViewFrom.Year, vm.ViewFrom.Month, 1);
        var firstLabel = true;
        for (var month = first; month <= vm.ViewTo; month = month.AddMonths(1))
        {
            var x0 = XOf(month);
            var x1 = XOf(month.AddMonths(1));
            var width = x1 - x0;
            var left = Math.Max(x0, HeaderWidth);
            if (x1 <= HeaderWidth) continue;
            dc.DrawLine(monthPen, new Point(Math.Round(x0) + 0.5, 4), new Point(Math.Round(x0) + 0.5, TimelineHeight));

            var withYear = month.Month == 1 || firstLabel;
            var name = width >= 110 ? month.ToString(withYear ? "MMMM yyyy" : "MMMM", inv)
                : width >= 54 ? month.ToString(withYear ? "MMM yyyy" : "MMM", inv)
                : width >= 26 ? month.ToString("MMM", inv)
                : month.Month == 1 ? month.ToString("yy", inv) : "";
            firstLabel = false;
            if (name.Length == 0) continue;
            var label = Text(name, 12.5, text, FontWeights.SemiBold, Math.Max(10, x1 - left - 8));
            dc.DrawText(label, new Point(left + 6, 5));
        }

        // Second line: days, or the Mondays of the weeks.
        if (pixelsPerDay >= 16)
        {
            for (var day = vm.ViewFrom.Date; day <= vm.ViewTo; day = day.AddDays(1))
            {
                var x = XOf(day);
                var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var label = Text(pixelsPerDay >= 34 ? day.ToString("ddd d", inv) : day.Day.ToString(inv), 10.5, weekend ? muted : text, FontWeights.Normal, 0);
                dc.DrawText(label, new Point(x + (pixelsPerDay - label.Width) / 2, 27));
            }
        }
        else if (pixelsPerDay >= 4.2)
        {
            var day = vm.ViewFrom.Date;
            while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
            for (; day <= vm.ViewTo; day = day.AddDays(7))
            {
                var label = Text(day.Day.ToString(inv), 10.5, muted, FontWeights.Normal, 0);
                dc.DrawText(label, new Point(XOf(day) + 3, 27));
            }
        }
        dc.Pop();

        // Corner above the row names.
        var corner = Text(vm.IsFreeView ? "Free view" : vm.RangeLabel, 11, muted, FontWeights.Normal, HeaderWidth - 16);
        dc.DrawText(corner, new Point(10, TimelineHeight - corner.Height - 8));
    }

    private void DrawBar(DrawingContext dc, RoadmapViewModel vm, RoadmapBar bar, double top, Color surface, Brush themeText,
        Brush selectedBrush, Brush selectedText)
    {
        var x0 = XOf(bar.Start);
        var x1 = XOf(bar.End);
        if (x1 < HeaderWidth - 2 || x0 > ActualWidth + 2) return;
        if (x1 - x0 < 6) x1 = x0 + 6;   // always something to see and to click
        var rect = new Rect(x0, top, x1 - x0, BarHeight);
        _barRects.Add((rect, bar));
        if (top + BarHeight < TimelineHeight || top > ActualHeight) return;

        var dark = HexToBrushConverter.Parse(bar.Color).Color;
        var light = Blend(dark, surface, 0.34);
        var selected = vm.IsSelected(bar);
        var clip = new RectangleGeometry(rect, 4, 4);
        var outline = bar.Segments.Any(s => s.Fill == BarFill.Outline);
        Brush labelBrush = themeText;

        dc.PushClip(clip);
        if (selected)
        {
            dc.DrawRectangle(selectedBrush, null, rect);
            labelBrush = selectedText;
        }
        else
        {
            foreach (var segment in bar.Segments)
            {
                var sx0 = Math.Max(XOf(segment.From), x0);
                var sx1 = Math.Min(Math.Max(XOf(segment.To), sx0), x1);
                if (segment == bar.Segments[^1]) sx1 = x1;
                if (sx1 <= sx0) continue;
                var piece = new Rect(sx0, top, sx1 - sx0, BarHeight);
                Brush fill = segment.Fill switch
                {
                    BarFill.Dark => Solid(dark),
                    BarFill.Light or BarFill.Outline => Solid(light),
                    BarFill.Striped => Stripes(dark, light),
                    BarFill.Mixed => MixedBrush(dark, segment.Color2),
                    _ => Solid(dark)
                };
                dc.DrawRectangle(fill, null, piece);
            }

            // The icon sits on the piece where the title starts: pick a color that reads on it.
            var under = bar.Segments.FirstOrDefault(s => s.From <= bar.LabelFrom && s.To > bar.LabelFrom) ?? bar.Segments[0];
            var background = under.Fill is BarFill.Dark ? dark : under.Fill is BarFill.Light or BarFill.Outline ? light : Blend(dark, light, 0.5);
            labelBrush = Luminance(background) > 0.58 ? Brushes.Black : Brushes.White;
        }

        // Icon (milestones) and title. The title can run over pieces of different colors: every piece
        // paints its part of the text in the color that reads on it.
        var labelX = Math.Max(Math.Max(XOf(bar.LabelFrom), x0), HeaderWidth) + 6;
        var available = Math.Min(x1 - labelX - 5, ActualWidth);   // a bar may end far outside the window
        if (bar.Kind == RoadmapBarKind.Milestone && available >= 14)
        {
            IconArt.Draw(dc, bar.Icon, vm.IconFolder, new Rect(labelX, top + (BarHeight - 15) / 2, 15, 15), labelBrush);
            labelX += 19;
            available -= 19;
        }
        if (available >= 12)
        {
            if (selected)
            {
                var title = Text(bar.Title, 12, labelBrush, FontWeights.SemiBold, available);
                dc.DrawText(title, new Point(labelX, top + (BarHeight - title.Height) / 2));
            }
            else
            {
                foreach (var segment in bar.Segments)
                {
                    var sx0 = Math.Max(XOf(segment.From), x0);
                    var sx1 = segment == bar.Segments[^1] ? x1 : Math.Min(Math.Max(XOf(segment.To), sx0), x1);
                    if (sx1 <= labelX || sx1 <= sx0) continue;
                    var on = segment.Fill switch
                    {
                        BarFill.Dark => dark,
                        BarFill.Light or BarFill.Outline => light,
                        BarFill.Mixed => Blend(dark, HexToBrushConverter.Parse(segment.Color2).Color, 0.5),
                        _ => Blend(dark, light, 0.5)
                    };
                    var brush = Luminance(on) > 0.58 ? Brushes.Black : Brushes.White;
                    var title = Text(bar.Title, 12, brush, FontWeights.SemiBold, available);
                    dc.PushClip(new RectangleGeometry(new Rect(sx0, top, sx1 - sx0, BarHeight)));
                    // Stripes are busy: a soft plate behind the letters keeps them readable.
                    if (segment.Fill is BarFill.Striped or BarFill.Mixed)
                    {
                        dc.PushOpacity(0.55);
                        dc.DrawRectangle(Solid(on), null, new Rect(labelX - 3, top + 3, Math.Min(title.Width + 6, available + 6), BarHeight - 6));
                        dc.Pop();
                    }
                    dc.DrawText(title, new Point(labelX, top + (BarHeight - title.Height) / 2));
                    dc.Pop();
                }
            }
        }
        dc.Pop();

        if (selected)
        {
            var pen = new Pen(Solid(dark), 2);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, rect, 4, 4);
        }
        else if (outline)
        {
            var pen = new Pen(Solid(dark), 1.6);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(rect.X + 0.8, rect.Y + 0.8, Math.Max(0, rect.Width - 1.6), rect.Height - 1.6), 4, 4);
        }
        else if (ReferenceEquals(bar, _hoverBar))
        {
            var pen = new Pen(Solid(Blend(dark, Colors.Black, 0.7)), 1);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, rect, 4, 4);
        }
    }

    /// <summary>The days two milestones share: stripes of both colors, or color and nothing when they are the same.</summary>
    private static Brush MixedBrush(Color color, string? otherHex)
    {
        var other = HexToBrushConverter.Parse(otherHex).Color;
        return other == color ? Stripes(color, Color.FromArgb(0, color.R, color.G, color.B)) : Stripes(color, other);
    }

    // ============================================================================ hit testing

    private RoadmapBar? BarAt(Point point)
    {
        if (point.Y < TimelineHeight || point.X < HeaderWidth) return null;
        for (var i = _barRects.Count - 1; i >= 0; i--)
            if (_barRects[i].Rect.Contains(point)) return _barRects[i].Bar;
        return null;
    }

    private (Rect Header, Rect Eye, Rect Area, RoadmapRow Row)? RowAt(Point point)
    {
        if (point.Y < TimelineHeight) return null;
        foreach (var row in _rowRects)
            if (point.Y >= row.Header.Top && point.Y <= row.Header.Bottom) return row;
        return null;
    }

    // ============================================================================ mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var point = e.GetPosition(this);

        if (_panning)
        {
            // Horizontal movement only in the free view; vertical movement always.
            if (vm.IsFreeView)
            {
                var days = (point.X - _panStart.X) / PlotWidth * vm.ViewDays;
                var target = _panFrom.AddDays(-days);
                vm.Pan((target - vm.ViewFrom).TotalDays);
            }
            _scrollY = _panScroll - (point.Y - _panStart.Y);
            ClampScroll();
            InvalidateVisual();
            return;
        }

        if (_dragRow != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && Math.Abs(point.Y - _dragStart.Y) > 5) _dragging = true;
            if (_dragging)
            {
                var tagRows = _rowRects.Where(r => r.Row.Kind == RoadmapRowKind.Tag).ToList();
                var index = tagRows.Count;
                for (var i = 0; i < tagRows.Count; i++)
                {
                    if (point.Y < tagRows[i].Header.Top + tagRows[i].Header.Height / 2) { index = i; break; }
                }
                if (index != _dropIndex)
                {
                    _dropIndex = index;
                    InvalidateVisual();
                }
                Cursor = Cursors.SizeNS;
            }
            return;
        }

        vm.SetHover(point.X >= HeaderWidth ? DateAt(point.X) : null);
        var bar = BarAt(point);
        if (!ReferenceEquals(bar, _hoverBar))
        {
            _hoverBar = bar;
            InvalidateVisual();
        }
        var overHeader = point.X < HeaderWidth && RowAt(point) != null;
        Cursor = bar != null || overHeader ? Cursors.Hand : null;
        ToolTip = bar is null ? null : BarToolTip(bar);
    }

    private static string BarToolTip(RoadmapBar bar)
    {
        var inv = CultureInfo.InvariantCulture;
        var end = bar.End.TimeOfDay == TimeSpan.Zero ? bar.End.AddDays(-1) : bar.End;
        var text = $"{bar.Title}\n{bar.Start.ToString("dd MMM yyyy", inv)} – {end.ToString("dd MMM yyyy", inv)}";
        if (bar.Segments.Any(s => s.Fill == BarFill.Striped)) text += "\nStriped = delay";
        return text;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _vm?.SetHover(null);
        if (_hoverBar != null)
        {
            _hoverBar = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        Focus();
        var point = e.GetPosition(this);

        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _panStart = point;
            _panFrom = vm.ViewFrom;
            _panScroll = _scrollY;
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (point.X < HeaderWidth)
            {
                if (RowAt(point) is { } hit)
                {
                    if (hit.Eye.Contains(point))
                    {
                        vm.ToggleCollapsed(hit.Row.Key);
                    }
                    else if (hit.Row.Kind == RoadmapRowKind.Tag)
                    {
                        _dragRow = hit.Row;
                        _dragStart = point;
                        _dragging = false;
                        _dropIndex = -1;
                        CaptureMouse();
                    }
                }
                e.Handled = true;
                return;
            }

            var bar = BarAt(point);
            vm.Select(bar);
            if (e.ClickCount == 2)
            {
                if (bar != null)
                {
                    // Deferred, so that the dialog opens after this mouse event is over.
                    var (id, kind) = (bar.Id, bar.Kind);
                    Dispatcher.BeginInvoke(new Action(() => vm.Activate(id, kind)));
                }
                else if (RowAt(point) is { Row.Collapsed: false } row)
                {
                    // An empty spot of the two special rows: create something that starts on that day.
                    var day = DateAt(point.X).Date;
                    if (row.Row.Kind == RoadmapRowKind.Milestones) Dispatcher.BeginInvoke(new Action(() => vm.EditMilestone(null, day)));
                    else if (row.Row.Kind == RoadmapRowKind.Custom) Dispatcher.BeginInvoke(new Action(() => vm.EditItem(null, day)));
                }
            }
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // The button was released somewhere else (another window, a dialog): no drag is going on any more.
        if (_panning || _dragRow != null)
        {
            _panning = false;
            _dragRow = null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var point = e.GetPosition(this);

        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = null;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _dragRow != null)
        {
            var row = _dragRow;
            var drop = _dragging ? _dropIndex : -1;
            _dragRow = null;
            _dragging = false;
            _dropIndex = -1;
            ReleaseMouseCapture();
            Cursor = null;
            if (drop >= 0)
            {
                // The position is counted without the dragged row itself.
                var tags = vm.Rows.Where(r => r.Kind == RoadmapRowKind.Tag).Select(r => r.Key).ToList();
                var from = tags.IndexOf(row.Key);
                vm.PlaceRow(row.Key, drop > from ? drop - 1 : drop);
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            ShowMenu(point);
            e.Handled = true;
        }
    }

    private void ShowMenu(Point point)
    {
        var vm = _vm;
        if (vm is null) return;
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        void Add(string header, Action action, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) item.InputGestureText = gesture;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (point.X < HeaderWidth)
        {
            if (RowAt(point) is not { } header) return;
            var key = header.Row.Key;
            Add(header.Row.Collapsed ? "Show this row" : "Hide this row", () => vm.ToggleCollapsed(key));
            if (header.Row.Kind == RoadmapRowKind.Tag)
            {
                menu.Items.Add(new Separator());
                Add("Move up", () => vm.MoveRow(key, -1), vm.CanMoveRow(key, -1));
                Add("Move down", () => vm.MoveRow(key, 1), vm.CanMoveRow(key, 1));
            }
            menu.IsOpen = true;
            return;
        }

        var bar = BarAt(point);
        if (bar != null)
        {
            vm.Select(bar);
            var (id, kind) = (bar.Id, bar.Kind);
            if (kind == RoadmapBarKind.Card)
            {
                Add("Open task…", () => vm.Activate(id, kind));
                menu.Items.Add(new Separator());
                Add("Remove Start/Finish dates", () => vm.RemoveDates(id));
            }
            else
            {
                Add("Edit…", () => vm.Activate(id, kind));
                menu.Items.Add(new Separator());
                Add("Delete", () => vm.Delete(id, kind), gesture: "Del");
            }
            menu.IsOpen = true;
            return;
        }

        if (RowAt(point) is { } row)
        {
            var day = DateAt(point.X).Date;
            if (row.Row.Kind == RoadmapRowKind.Milestones) Add("New milestone here…", () => vm.EditMilestone(null, day));
            else if (row.Row.Kind == RoadmapRowKind.Custom) Add("New task here…", () => vm.EditItem(null, day));
            else return;
            menu.IsOpen = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var point = e.GetPosition(this);
        if (vm.IsFreeView)
        {
            var pivot = point.X >= HeaderWidth ? DateAt(point.X) : vm.ViewFrom.AddDays(vm.ViewDays / 2);
            vm.Zoom(e.Delta > 0 ? 1 / 1.18 : 1.18, pivot);
        }
        else
        {
            _scrollY -= e.Delta / 120.0 * 48;
            ClampScroll();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    // ============================================================================ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Left:
                if (vm.IsFreeView) vm.Step(-1, shift); else vm.PreviousCommand.Execute(null);
                break;
            case Key.Right:
                if (vm.IsFreeView) vm.Step(1, shift); else vm.NextCommand.Execute(null);
                break;
            case Key.Up:
                _scrollY -= shift ? 160 : 36;
                ClampScroll();
                InvalidateVisual();
                break;
            case Key.Down:
                _scrollY += shift ? 160 : 36;
                ClampScroll();
                InvalidateVisual();
                break;
            case Key.Delete:
                vm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.Enter:
                vm.EditSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                vm.ClearSelection();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
