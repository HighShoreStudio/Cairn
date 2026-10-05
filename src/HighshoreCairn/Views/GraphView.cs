using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HighshoreCairn.Converters;
using HighshoreCairn.Services;

namespace HighshoreCairn.Views;

/// <summary>
/// Draws a <see cref="GraphModel"/> like the graph view of Obsidian: round nodes with their name below,
/// thin lines for the links. Mouse wheel = zoom, drag the background = move the view, drag a node =
/// move it, click a node = select it (the host shows its preview), double-click = open it.
/// The layout itself is computed by GraphModel; this control only animates and paints it.
/// </summary>
public class GraphView : FrameworkElement
{
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(GraphModel), typeof(GraphView), new PropertyMetadata(null, OnGraphChanged));

    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(
        nameof(SelectedId), typeof(string), typeof(GraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly string[] GroupColors =
        { "#6CC5E0", "#8FB0FF", "#C3A6F5", "#F29BCB", "#F2B866", "#A6D96A", "#5ED3A5", "#F28B8B", "#E6C84F", "#7FD1C6" };

    private readonly DispatcherTimer _timer;
    private double _zoom = 1;
    private Vector _offset;
    private bool _autoFit = true;   // the view follows the graph until the user moves or zooms it
    private GraphNode? _hover;
    private GraphNode? _dragNode;
    private bool _panning;
    private bool _moved;
    private Point _downScreen;
    private Vector _downOffset;

    public GraphView()
    {
        ClipToBounds = true;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Animate();
        Loaded += (_, _) => { ThemeService.ThemeChanged -= OnThemeChanged; ThemeService.ThemeChanged += OnThemeChanged; StartIfNeeded(); };
        Unloaded += (_, _) => { ThemeService.ThemeChanged -= OnThemeChanged; _timer.Stop(); };
        IsVisibleChanged += (_, _) => StartIfNeeded();
        SizeChanged += (_, _) => { if (_autoFit) FitCore(); };
    }

    public GraphModel? Graph
    {
        get => (GraphModel?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    /// <summary>Id of the highlighted node.</summary>
    public string? SelectedId
    {
        get => (string?)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>A node was clicked (null = the background was clicked).</summary>
    public event Action<string?>? NodeSelected;

    /// <summary>A node was double-clicked.</summary>
    public event Action<string>? NodeActivated;

    private static void OnGraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (GraphView)d;
        view._hover = null;
        view._dragNode = null;
        view.Fit();
        view.StartIfNeeded();
        view.InvalidateVisual();
    }

    private void OnThemeChanged() => InvalidateVisual();

    private void StartIfNeeded()
    {
        if (IsVisible && IsLoaded && Graph is { IsSettled: false }) _timer.Start();
    }

    private void Animate()
    {
        var graph = Graph;
        if (graph is null || !IsVisible)
        {
            _timer.Stop();
            return;
        }
        // Two simulation steps per frame: smooth and quick to settle.
        var running = graph.Step() && graph.Step();
        if (_autoFit) FitCore(); // keep the whole graph in view while it settles
        InvalidateVisual();
        if (!running && _dragNode is null) _timer.Stop();
    }

    // ------------------------------------------------------------------ view

    private Point ToScreen(double x, double y) => new(x * _zoom + _offset.X, y * _zoom + _offset.Y);

    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);

    /// <summary>Shows the whole graph (and keeps doing so until the user moves the view).</summary>
    public void Fit()
    {
        _autoFit = true;
        FitCore();
    }

    private void FitCore()
    {
        var graph = Graph;
        if (graph is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (left, top, right, bottom) = graph.Bounds();
        const double margin = 60;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        _zoom = Math.Clamp(Math.Min((ActualWidth - 2 * margin) / width, (ActualHeight - 2 * margin) / height), 0.15, 1.6);
        _offset = new Vector(ActualWidth / 2 - (left + right) / 2 * _zoom, ActualHeight / 2 - (top + bottom) / 2 * _zoom);
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);

    private void ZoomAt(Point screen, double factor)
    {
        _autoFit = false;
        var anchor = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, 0.1, 5);
        _offset = new Vector(screen.X - anchor.X * _zoom, screen.Y - anchor.Y * _zoom);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var graph = Graph;
        if (graph is null) return;
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        var node = graph.HitTest(world.X, world.Y, 4 / _zoom);

        if (e.ClickCount == 2 && node != null)
        {
            NodeActivated?.Invoke(node.Id);
            e.Handled = true;
            return;
        }

        _downScreen = screen;
        _downOffset = _offset;
        _moved = false;
        _dragNode = node;
        _panning = node is null;
        if (node != null) node.Pinned = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var graph = Graph;
        if (graph is null) return;
        var screen = e.GetPosition(this);

        if (IsMouseCaptured)
        {
            if ((screen - _downScreen).Length > 3) _moved = true;
            if (_dragNode != null && _moved)
            {
                var world = ToWorld(screen);
                _dragNode.X = world.X;
                _dragNode.Y = world.Y;
                graph.Reheat(0.25);
                _timer.Start();
            }
            else if (_panning && _moved)
            {
                _autoFit = false;
                _offset = _downOffset + (screen - _downScreen);
            }
            InvalidateVisual();
            return;
        }

        var point = ToWorld(screen);
        var hover = graph.HitTest(point.X, point.Y, 4 / _zoom);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover != null ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        var node = _dragNode;
        if (node != null) node.Pinned = false;
        _dragNode = null;
        _panning = false;
        if (!_moved) NodeSelected?.Invoke(node?.Id);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ painting

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Brush GroupBrush(GraphNode node, Brush neutral)
    {
        if (!string.IsNullOrEmpty(node.Color)) return HexToBrushConverter.Parse(node.Color);
        if (string.IsNullOrEmpty(node.Group)) return neutral;
        var hash = 0;
        foreach (var ch in node.Group) hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return HexToBrushConverter.Parse(GroupColors[Math.Abs(hash % GroupColors.Length)]);
    }

    /// <summary>A five-pointed star around a center.</summary>
    internal static Geometry Star(Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var angle = -Math.PI / 2 + i * Math.PI / 5;
                var r = i % 2 == 0 ? radius : radius * 0.45;
                var point = new Point(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r);
                if (i == 0) context.BeginFigure(point, true, true);
                else context.LineTo(point, true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var background = Find("Brush.Canvas", Brushes.White);
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var graph = Graph;
        if (graph is null || graph.Nodes.Count == 0) return;

        var edgeBrush = Find("Brush.Scroll", Brushes.LightGray);
        var highlight = Find("Brush.Primary", Brushes.SteelBlue);
        var textBrush = Find("Brush.TextMuted", Brushes.Gray);
        var strongText = Find("Brush.Text", Brushes.Black);
        var neutral = Find("Brush.TextMuted", Brushes.Gray);
        var selection = Find("Brush.Selection", Brushes.DodgerBlue);

        var focus = _dragNode ?? _hover ?? (SelectedId is null ? null : graph.Find(SelectedId));
        var edgePen = new Pen(edgeBrush, 1);
        var edgeHot = new Pen(highlight, 1.6);
        edgePen.Freeze();
        edgeHot.Freeze();

        // Links first, so that the nodes cover their ends.
        foreach (var edge in graph.Edges)
        {
            var hot = focus != null && (edge.A == focus || edge.B == focus);
            if (focus != null && !hot) dc.PushOpacity(0.35);
            dc.DrawLine(hot ? edgeHot : edgePen, ToScreen(edge.A.X, edge.A.Y), ToScreen(edge.B.X, edge.B.Y));
            if (focus != null && !hot) dc.Pop();
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var showLabels = _zoom >= 0.45;
        var starBrush = Find("Brush.Star", Brushes.Gold);
        var starPen = new Pen(Brushes.Black, 1) { LineJoin = PenLineJoin.Round };   // black in both themes
        starPen.Freeze();
        var starFillsNode = _zoom < 0.55;

        foreach (var node in graph.Nodes)
        {
            var center = ToScreen(node.X, node.Y);
            var radius = Math.Max(2.5, node.Radius * Math.Min(1.6, Math.Max(0.45, _zoom)));
            if (center.X < -80 || center.Y < -80 || center.X > ActualWidth + 80 || center.Y > ActualHeight + 80) continue;

            var near = focus is null || node == focus || focus.Neighbors.Contains(node);
            if (!near) dc.PushOpacity(0.25);

            var fill = GroupBrush(node, neutral);
            if (node.IsGhost)
            {
                var ghostPen = new Pen(fill, 1.4) { DashStyle = DashStyles.Dash };
                dc.DrawEllipse(background, ghostPen, center, radius, radius);
            }
            else
            {
                dc.DrawEllipse(fill, null, center, radius, radius);
            }

            // A dot in the middle: the item has references (to tasks or documents).
            if (node.HasDot && !(node.Starred && starFillsNode))
                dc.DrawEllipse(background, null, center, Math.Max(1.6, radius * 0.36), Math.Max(1.6, radius * 0.36));

            // Favorites: a star on the outline of the node; when the view is zoomed far out the star
            // takes the place of the whole node, so it can still be spotted.
            if (node.Starred)
            {
                if (starFillsNode)
                    dc.DrawGeometry(starBrush, starPen, Star(center, Math.Max(5, radius * 1.25)));
                else
                {
                    var corner = new Point(center.X + radius * 0.74, center.Y - radius * 0.74);
                    dc.DrawGeometry(starBrush, starPen, Star(corner, Math.Clamp(radius * 0.62, 5.5, 9)));
                }
            }

            var isSelected = SelectedId != null && node.Id == SelectedId;
            if (isSelected || node == _hover)
                dc.DrawEllipse(null, new Pen(isSelected ? selection : highlight, 2), center, radius + 3, radius + 3);

            if (showLabels || node == focus || isSelected)
            {
                var label = node.Label.Length > 34 ? node.Label[..34] + "…" : node.Label;
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    Math.Clamp(11 * Math.Sqrt(_zoom), 9, 14), node == focus || isSelected ? strongText : textBrush, dpi);
                dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y + radius + 3));
            }

            if (!near) dc.Pop();
        }
    }
}
