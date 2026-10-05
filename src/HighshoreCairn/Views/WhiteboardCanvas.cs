using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HighshoreCairn.Converters;
using HighshoreCairn.Models;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;
using Path = System.Windows.Shapes.Path;   // not System.IO.Path

namespace HighshoreCairn.Views;

/// <summary>
/// The drawing surface of the whiteboard. It is pure "view": it draws the elements of
/// WhiteboardViewModel.Data and turns mouse / keyboard input into changes of that data.
///
/// Layers (bottom to top):
///   grid dots  →  world (connectors, elements, preview; panned and zoomed)  →  overlay (selection frames, handles)
///
/// Coordinates: "world" = whiteboard units stored in the file, "screen" = pixels of this control.
/// </summary>
public class WhiteboardCanvas : Grid
{
    private const string ClipboardFormat = "HighshoreCairn.WhiteboardElements";
    private const double MinZoom = 0.1, MaxZoom = 8;

    private readonly Rectangle _gridLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _world = new();
    private readonly Canvas _connectorLayer = new();
    private readonly Canvas _elementLayer = new();
    private readonly Canvas _previewLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _overlay = new();
    private readonly MatrixTransform _view = new();

    private readonly Dictionary<string, FrameworkElement> _hosts = new();
    private readonly Dictionary<string, BitmapSource?> _imageCache = new();

    private WhiteboardViewModel? _vm;
    private double _zoom = 1;
    private Vector _offset = new(40, 40);
    private bool _fitPending = true;

    // ---- what the mouse is currently doing
    private enum Mode { None, Pan, Move, Resize, Rotate, Marquee, Shape, Pen, Poly, Connect }

    private Mode _mode;
    private Point _downScreen;
    private WbPoint _downWorld;
    private Vector _panStart;
    private bool _changed;                       // the current drag already modified the data (snapshot taken)
    private Dictionary<string, WbPoint> _moveOrigins = new();
    private WbElement? _active;                  // element being resized / rotated
    private int _handleX, _handleY;
    private readonly List<WbPoint> _points = new();   // pen / polyline points (world)
    private WbPoint _lastMouseWorld;
    private WbElement? _connectFrom;
    private WbElement? _pendingConnect;          // click-click connection: first element already chosen
    private Rect? _marquee;
    private bool _spaceDown;

    // ---- text editing
    private WbElement? _editing;
    private TextBox? _editBox;
    private bool _editIsNew;
    private bool _endingEdit;

    private sealed record HandleTag(int X, int Y, bool Rotate);

    public WhiteboardCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        AllowDrop = true;
        SetResourceReference(BackgroundProperty, "Brush.Canvas");

        _world.RenderTransform = _view;
        _world.Children.Add(_connectorLayer);
        _world.Children.Add(_elementLayer);
        _world.Children.Add(_previewLayer);
        Children.Add(_gridLayer);
        Children.Add(_world);
        Children.Add(_overlay);

        DataContextChanged += (_, e) => Attach(e.NewValue as WhiteboardViewModel);
        SizeChanged += (_, _) =>
        {
            if (_fitPending && ActualWidth > 0 && IsVisible) Fit();
            UpdateGrid();
            RefreshOverlay();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            if (_fitPending && ActualWidth > 0) Fit();
            Dispatcher.BeginInvoke(new Action(() => Focus()));
        };
        Loaded += (_, _) => ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    // ================================================================== view model wiring

    private void Attach(WhiteboardViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.DocumentChanged -= SyncAll;
            _vm.SelectionChanged -= OnSelectionChanged;
            _vm.ViewRequested -= OnViewRequested;
            _vm.ToolChanged -= OnToolChanged;
        }
        _vm = vm;
        _hosts.Clear();
        _imageCache.Clear();
        if (_vm is null)
        {
            _elementLayer.Children.Clear();
            _connectorLayer.Children.Clear();
            _overlay.Children.Clear();
            return;
        }
        _vm.DocumentChanged += SyncAll;
        _vm.SelectionChanged += OnSelectionChanged;
        _vm.ViewRequested += OnViewRequested;
        _vm.ToolChanged += OnToolChanged;
        _fitPending = true;
        SyncAll();
        OnToolChanged();
        if (ActualWidth > 0 && IsVisible) Fit();
    }

    private void OnThemeChanged()
    {
        SyncAll();
        UpdateGrid();
    }

    private void OnSelectionChanged()
    {
        BuildConnectors();
        RefreshOverlay();
    }

    private void OnToolChanged()
    {
        if (_vm is null) return;
        EndEdit();
        CancelInteraction();
        _pendingConnect = null;
        OnToolCursor();
        RefreshOverlay();
    }

    private void OnViewRequested(string request)
    {
        switch (request)
        {
            case "zoomin": ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1.25); break;
            case "zoomout": ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / 1.25); break;
            case "reset": ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / _zoom); break;
            case "fit": if (ActualWidth > 0 && IsVisible) Fit(); else _fitPending = true; break;
            case "focus": Dispatcher.BeginInvoke(new Action(FocusSelection), System.Windows.Threading.DispatcherPriority.Loaded); break;
            case "grid": UpdateGrid(); break;
            case "export": ExportPng(); break;
            case "addimage": AddImageFromDialog(); break;
        }
    }

    // ================================================================== coordinates & view

    private Point ToScreen(WbPoint world) => new(world.X * _zoom + _offset.X, world.Y * _zoom + _offset.Y);

    private WbPoint ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);

    private WbPoint ViewCenter => ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

    private void ApplyView()
    {
        _view.Matrix = new Matrix(_zoom, 0, 0, _zoom, _offset.X, _offset.Y);
        if (_vm != null) _vm.ZoomText = $"{Math.Round(_zoom * 100)}%";
        UpdateGrid();
        RefreshOverlay();
    }

    private void ZoomAt(Point screen, double factor)
    {
        var anchor = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _offset = new Vector(screen.X - anchor.X * _zoom, screen.Y - anchor.Y * _zoom);
        ApplyView();
    }

    /// <summary>Shows the whole drawing.</summary>
    private void Fit()
    {
        _fitPending = false;
        if (_vm is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_vm.Data.Elements.Count == 0)
        {
            _zoom = 1;
            _offset = new Vector(40, 40);
        }
        else
        {
            var bounds = WhiteboardOps.Bounds(_vm.Data.Elements);
            const double margin = 48;
            var zoomX = (ActualWidth - 2 * margin) / Math.Max(1, bounds.Width);
            var zoomY = (ActualHeight - 2 * margin) / Math.Max(1, bounds.Height);
            _zoom = Math.Clamp(Math.Min(zoomX, zoomY), MinZoom, 1.5);
            _offset = new Vector(
                (ActualWidth - bounds.Width * _zoom) / 2 - bounds.Left * _zoom,
                (ActualHeight - bounds.Height * _zoom) / 2 - bounds.Top * _zoom);
        }
        ApplyView();
    }

    /// <summary>Centers the view on the selected elements (used by "Show on board" of the graph view).</summary>
    private void FocusSelection()
    {
        if (_vm is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var selected = _vm.SelectedElements.ToList();
        if (selected.Count == 0) return;
        var bounds = WhiteboardOps.Bounds(selected);
        const double margin = 120;
        var zoomX = (ActualWidth - 2 * margin) / Math.Max(1, bounds.Width);
        var zoomY = (ActualHeight - 2 * margin) / Math.Max(1, bounds.Height);
        _zoom = Math.Clamp(Math.Min(zoomX, zoomY), MinZoom, 1.25);
        _offset = new Vector(
            (ActualWidth - bounds.Width * _zoom) / 2 - bounds.Left * _zoom,
            (ActualHeight - bounds.Height * _zoom) / 2 - bounds.Top * _zoom);
        ApplyView();
        Focus();
    }

    /// <summary>Draws one element on its own, as a picture (the preview of a node of the graph view).</summary>
    public ImageSource? RenderElement(WbElement? element, double maxSize = 300)
    {
        if (element is null) return null;
        try
        {
            const double margin = 10;
            var bounds = WhiteboardOps.Bounds(element);
            var width = Math.Max(1, bounds.Width) + 2 * margin;
            var height = Math.Max(1, bounds.Height) + 2 * margin;
            var scale = Math.Clamp(maxSize / Math.Max(width, height), 0.05, 2);

            var root = new Canvas { Width = width, Height = height };
            root.SetResourceReference(BackgroundProperty, "Brush.Canvas");
            var content = new Canvas { RenderTransform = new TranslateTransform(margin - bounds.Left, margin - bounds.Top) };
            content.Children.Add(BuildVisual(element));
            root.Children.Add(content);
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dotted background that follows pan and zoom.</summary>
    private void UpdateGrid()
    {
        if (_vm is null || !_vm.ShowGrid)
        {
            _gridLayer.Fill = null;
            return;
        }
        double step = 24;
        while (step * _zoom < 14) step *= 2;
        var size = step * _zoom;
        var dot = (TryFindResource("Brush.Grid") as Brush) ?? Brushes.LightGray;
        var drawing = new GeometryDrawing(dot, null, new EllipseGeometry(new Point(1.2, 1.2), 1.2, 1.2));
        _gridLayer.Fill = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(_offset.X % size, _offset.Y % size, size, size)
        };
    }

    // ================================================================== drawing the document

    /// <summary>Rebuilds every visual from the data. Called after each committed change.</summary>
    private void SyncAll()
    {
        if (_vm is null) return;
        if (_editing != null && _vm.Data.Elements.All(e => e.Id != _editing.Id)) AbandonEdit();

        _elementLayer.Children.Clear();
        _hosts.Clear();
        foreach (var element in _vm.Data.Elements)
        {
            var host = BuildVisual(element);
            _hosts[element.Id] = host;
            _elementLayer.Children.Add(host);
        }
        SyncTextHeights();
        BuildConnectors();
        RefreshOverlay();
    }

    /// <summary>Text boxes grow with their content: copy the measured height back into the model.</summary>
    private void SyncTextHeights()
    {
        if (_vm is null) return;
        var any = _vm.Data.Elements.Any(e => e.Kind == WbKind.Text);
        if (!any) return;
        _elementLayer.UpdateLayout();
        foreach (var element in _vm.Data.Elements)
        {
            if (element.Kind != WbKind.Text || !_hosts.TryGetValue(element.Id, out var host)) continue;
            if (host.ActualHeight > 0 && Math.Abs(host.ActualHeight - element.Height) > 0.5)
                element.Height = Math.Round(host.ActualHeight, 1);
        }
    }

    private void RefreshElement(WbElement element)
    {
        if (!_hosts.TryGetValue(element.Id, out var old)) return;
        var index = _elementLayer.Children.IndexOf(old);
        var host = BuildVisual(element);
        _hosts[element.Id] = host;
        if (index >= 0)
        {
            _elementLayer.Children.RemoveAt(index);
            _elementLayer.Children.Insert(index, host);
        }
    }

    private static void PlaceVisual(FrameworkElement host, WbElement element)
    {
        Canvas.SetLeft(host, element.X);
        Canvas.SetTop(host, element.Y);
        host.RenderTransform = new RotateTransform(element.Rotation);
    }

    /// <summary>Creates the WPF visual of an element (recursively for groups).</summary>
    private FrameworkElement BuildVisual(WbElement element)
    {
        FrameworkElement host;
        switch (element.Kind)
        {
            case WbKind.Text:
                host = BuildText(element);
                break;

            case WbKind.Image:
            {
                var canvas = new Canvas { Width = element.Width, Height = element.Height, Background = Brushes.Transparent };
                var source = LoadImage(element.Image);
                if (source != null)
                {
                    var image = new Image { Source = source, Stretch = Stretch.Fill, Width = element.Width, Height = element.Height };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    canvas.Children.Add(image);
                }
                else
                {
                    var missing = new Border
                    {
                        Width = element.Width, Height = element.Height, BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text = "image not found", HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center, FontSize = 11
                        }
                    };
                    missing.SetResourceReference(Border.BorderBrushProperty, "Brush.Danger");
                    missing.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextMuted");
                    canvas.Children.Add(missing);
                }
                host = canvas;
                break;
            }

            case WbKind.Group:
            {
                var canvas = new Canvas { Width = element.Width, Height = element.Height };
                // Children live in a RefWidth x RefHeight space that is stretched to the group box.
                var inner = new Canvas
                {
                    RenderTransform = new ScaleTransform(
                        element.RefWidth > 0 ? element.Width / element.RefWidth : 1,
                        element.RefHeight > 0 ? element.Height / element.RefHeight : 1)
                };
                foreach (var child in element.Children ?? new List<WbElement>()) inner.Children.Add(BuildVisual(child));
                canvas.Children.Add(inner);
                host = canvas;
                break;
            }

            default:
            {
                // A Canvas does not clip its children, so thick outlines can extend beyond the box.
                var canvas = new Canvas { Width = element.Width, Height = element.Height };
                var geometry = BuildGeometry(element, element.Width, element.Height);

                var shape = new Path
                {
                    Data = geometry,
                    StrokeThickness = element.StrokeWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                SetStroke(shape, element.Stroke);
                if (element.Fill != null) shape.Fill = HexToBrushConverter.Parse(element.Fill);

                // Invisible, thicker copy: makes thin lines easy to click.
                var hit = new Path
                {
                    Data = geometry,
                    Stroke = Brushes.Transparent,
                    StrokeThickness = Math.Max(element.StrokeWidth + 10, 14),
                    // Closed shapes can be grabbed from the inside too, even without a fill color.
                    Fill = element.Fill != null || element.Closed ||
                           element.Kind is WbKind.Rectangle or WbKind.Ellipse or WbKind.Triangle or WbKind.Polygon
                        ? Brushes.Transparent
                        : null
                };
                canvas.Children.Add(shape);
                canvas.Children.Add(hit);
                host = canvas;
                break;
            }
        }

        host.Tag = element;
        host.RenderTransformOrigin = new Point(0.5, 0.5);
        PlaceVisual(host, element);
        return host;
    }

    private FrameworkElement BuildText(WbElement element)
    {
        var block = new TextBlock
        {
            Text = element.Text ?? "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = element.FontSize > 0 ? element.FontSize : 16
        };
        SetTextColor(block, element);

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Background = element.Fill != null ? HexToBrushConverter.Parse(element.Fill) : Brushes.Transparent,
            Child = block
        };

        // Width is fixed (the text wraps), the height grows with the text.
        return new Grid { Width = element.Width, MinHeight = element.Height, Children = { border } };
    }

    /// <summary>Text color: explicit, or readable on the fill, or the theme text color.</summary>
    private static void SetTextColor(FrameworkElement target, WbElement element)
    {
        if (element.Stroke != null)
        {
            target.SetValue(TextElement.ForegroundProperty, HexToBrushConverter.Parse(element.Stroke));
        }
        else if (element.Fill != null)
        {
            var color = HexToBrushConverter.Parse(element.Fill).Color;
            var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            target.SetValue(TextElement.ForegroundProperty, luminance > 0.55 ? Brushes.Black : Brushes.White);
        }
        else
        {
            target.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        }
    }

    /// <summary>null = automatic color: bound to the theme text brush, so it flips with dark mode.</summary>
    private static void SetStroke(Shape shape, string? hex)
    {
        if (hex is null) shape.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
        else shape.Stroke = HexToBrushConverter.Parse(hex);
    }

    private static Geometry BuildGeometry(WbElement element, double w, double h)
    {
        Geometry geometry;
        switch (element.Kind)
        {
            case WbKind.Rectangle:
                geometry = new RectangleGeometry(new Rect(0, 0, w, h));
                break;
            case WbKind.Ellipse:
                geometry = new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2);
                break;
            case WbKind.Triangle:
                geometry = PolyGeometry(new[] { new Point(w / 2, 0), new Point(w, h), new Point(0, h) }, closed: true, filled: true);
                break;
            default:
                var points = (element.Points ?? new List<WbPoint>()).Select(p => new Point(p.X * w, p.Y * h)).ToList();
                if (points.Count < 2) points = new List<Point> { new(0, 0), new(w, h) };
                // Always a "fillable" figure: whether it is painted depends on the Fill brush of the Path
                // (the invisible hit-test copy uses it to make closed drawings clickable from the inside).
                geometry = PolyGeometry(points, element.Kind == WbKind.Polygon || element.Closed, filled: true);
                break;
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry PolyGeometry(IReadOnlyList<Point> points, bool closed, bool filled)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], filled, closed);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        return geometry;
    }

    private BitmapSource? LoadImage(string? file)
    {
        if (_vm is null || string.IsNullOrEmpty(file)) return null;
        if (_imageCache.TryGetValue(file, out var cached)) return cached;
        BitmapSource? result = null;
        try
        {
            var path = _vm.Service.ImagePath(file);
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read now, do not keep the file locked
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                result = bitmap;
            }
        }
        catch
        {
            result = null;
        }
        _imageCache[file] = result;
        return result;
    }

    // ------------------------------------------------------------------ connectors

    private void BuildConnectors()
    {
        _connectorLayer.Children.Clear();
        if (_vm is null) return;
        foreach (var connector in _vm.Data.Connectors)
        {
            var from = WhiteboardOps.Find(_vm.Data, connector.FromId);
            var to = WhiteboardOps.Find(_vm.Data, connector.ToId);
            if (from is null || to is null) continue;

            // From edge to edge, along the line between the two centers.
            var a = WhiteboardOps.EdgePoint(from, to.Center);
            var b = WhiteboardOps.EdgePoint(to, from.Center);
            var start = new Point(a.X, a.Y);
            var end = new Point(b.X, b.Y);
            var selected = connector.Id == _vm.SelectedConnectorId;

            var line = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                StrokeThickness = selected ? connector.Thickness + 1.5 : connector.Thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            if (selected) line.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
            else SetStroke(line, connector.Color);
            _connectorLayer.Children.Add(line);

            if (connector.Arrow)
            {
                var direction = end - start;
                if (direction.Length > 1)
                {
                    direction.Normalize();
                    var normal = new Vector(-direction.Y, direction.X);
                    var size = 7 + connector.Thickness * 2.5;
                    var head = new Polygon
                    {
                        Points = new PointCollection { end, end - direction * size + normal * size * 0.45, end - direction * size - normal * size * 0.45 },
                        IsHitTestVisible = false
                    };
                    if (selected) head.SetResourceReference(Shape.FillProperty, "Brush.Selection");
                    else if (connector.Color is null) head.SetResourceReference(Shape.FillProperty, "Brush.Text");
                    else head.Fill = HexToBrushConverter.Parse(connector.Color);
                    _connectorLayer.Children.Add(head);
                }
            }

            _connectorLayer.Children.Add(new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = Brushes.Transparent, StrokeThickness = Math.Max(connector.Thickness + 10, 12),
                Tag = connector, Cursor = Cursors.Hand
            });
        }
    }

    // ------------------------------------------------------------------ overlay (selection, handles)

    private void RefreshOverlay()
    {
        _overlay.Children.Clear();
        if (_vm is null) return;

        var selected = _vm.SelectedElements.ToList();
        foreach (var element in selected) _overlay.Children.Add(SelectionFrame(element, dashed: selected.Count > 1));

        if (selected.Count == 1 && _vm.Tool == WbTool.Select && _editing is null && _mode is Mode.None or Mode.Resize or Mode.Rotate)
            AddHandles(selected[0]);

        var source = _connectFrom ?? _pendingConnect;
        if (source != null) _overlay.Children.Add(SelectionFrame(source, dashed: false, thickness: 2));

        if (_marquee is { } rect)
        {
            var box = new Rectangle
            {
                Width = rect.Width, Height = rect.Height, StrokeThickness = 1, IsHitTestVisible = false,
                StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.9
            };
            box.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            _overlay.Children.Add(box);
        }
    }

    private Polygon SelectionFrame(WbElement element, bool dashed, double thickness = 1.2)
    {
        var polygon = new Polygon
        {
            Points = new PointCollection(WhiteboardOps.Corners(element).Select(ToScreen)),
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        if (dashed) polygon.StrokeDashArray = new DoubleCollection { 4, 3 };
        polygon.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
        return polygon;
    }

    private void AddHandles(WbElement element)
    {
        double hw = element.Width / 2, hh = element.Height / 2;

        // Rotation handle: above the top edge, connected by a thin line.
        var top = ToScreen(WhiteboardOps.LocalToWorld(element, new WbPoint(0, -hh)));
        var up = WhiteboardOps.Rotate(new WbPoint(0, -26), element.Rotation);
        var knob = new Point(top.X + up.X, top.Y + up.Y);
        var stem = new Line { X1 = top.X, Y1 = top.Y, X2 = knob.X, Y2 = knob.Y, StrokeThickness = 1, IsHitTestVisible = false };
        stem.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
        _overlay.Children.Add(stem);
        _overlay.Children.Add(Handle(knob, new HandleTag(0, 0, true), round: true, Cursors.Hand, "Rotate (Shift: 15° steps)"));

        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0) continue;
                var position = ToScreen(WhiteboardOps.LocalToWorld(element, new WbPoint(x * hw, y * hh)));
                var cursor = x == 0 ? Cursors.SizeNS : y == 0 ? Cursors.SizeWE : x == y ? Cursors.SizeNWSE : Cursors.SizeNESW;
                _overlay.Children.Add(Handle(position, new HandleTag(x, y, false), round: false, cursor, "Resize (Shift: toggle proportions)"));
            }
        }
    }

    private Shape Handle(Point center, HandleTag tag, bool round, Cursor cursor, string toolTip)
    {
        const double size = 10;
        Shape handle = round ? new Ellipse() : new Rectangle();
        handle.Width = handle.Height = size;
        handle.StrokeThickness = 1.5;
        handle.Tag = tag;
        handle.Cursor = cursor;
        handle.ToolTip = toolTip;
        handle.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
        handle.SetResourceReference(Shape.FillProperty, "Brush.Surface");
        Canvas.SetLeft(handle, center.X - size / 2);
        Canvas.SetTop(handle, center.Y - size / 2);
        return handle;
    }

    // ================================================================== hit testing

    /// <summary>What is under a screen point: a handle, a connector, or a (top-level) element.</summary>
    private object? HitTest(Point screen)
    {
        var hit = InputHitTest(screen) as DependencyObject;
        WbElement? element = null;
        while (hit != null && hit != this)
        {
            if (hit is FrameworkElement fe)
            {
                if (fe.Tag is HandleTag or WbConnector) return fe.Tag;
                if (fe.Tag is WbElement tagged) element = tagged;   // keep climbing: the outermost one is the top-level element
            }
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return element;
    }

    private WbElement? HitElement(Point screen) => HitTest(screen) as WbElement;

    /// <summary>Like HitElement, but anywhere inside the box of an element counts (used to connect elements).</summary>
    private WbElement? HitElementLoose(Point screen)
    {
        var exact = HitElement(screen);
        if (exact != null || _vm is null) return exact;
        var world = ToWorld(screen);
        return _vm.Data.Elements.LastOrDefault(e => WhiteboardOps.Contains(e, world));
    }

    // ================================================================== context menu (right button)

    private WbElement? _menuTarget;

    /// <summary>
    /// The menu of the selected element(s): Duplicate, Delete, Disconnect and Merge work on any selection;
    /// Split and View in Graph are offered for a single element only.
    /// </summary>
    private void ShowElementMenu()
    {
        if (_vm is null || _vm.SelectedIds.Count == 0) return;
        var single = _vm.SelectedIds.Count == 1;
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        MenuItem Item(string header, ICommand command, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Command = command, IsEnabled = enabled };
            if (gesture != null) item.InputGestureText = gesture;
            menu.Items.Add(item);
            return item;
        }

        Item("Duplicate", _vm.DuplicateCommand, gesture: "Ctrl+D");
        Item("Delete", _vm.DeleteCommand, gesture: "Del");
        Item("Disconnect", _vm.DisconnectCommand, _vm.CanDisconnect).ToolTip = "Removes every line or arrow connected to the selection";
        Item("Merge", _vm.GroupCommand, _vm.CanGroup, "Ctrl+G");
        if (single)
        {
            if (_vm.CanUngroup) Item("Split", _vm.UngroupCommand, gesture: "Ctrl+Shift+G");
            menu.Items.Add(new Separator());
            Item("View in Graph", _vm.ViewInGraphCommand).ToolTip = "Opens the graph view with the node of this element selected";
        }
        menu.Items.Add(new Separator());
        Item(_vm.IsFavorite ? "Remove from favorites" : "Add to favorites", _vm.ToggleFavoriteCommand);

        menu.IsOpen = true;
    }

    // ================================================================== mouse

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_vm is null) return;

        var screen = e.GetPosition(this);
        var world = ToWorld(screen);

        // A click inside the text being edited belongs to the text box.
        if (_editing != null && HitElement(screen) == _editing) return;

        // Another button pressed in the middle of a drag (moving, drawing, panning) does not disturb it.
        if (_mode != Mode.None && _mode != Mode.Poly && e.ChangedButton != MouseButton.Left)
        {
            e.Handled = true;
            return;
        }

        EndEdit();
        Focus();
        _downButton = e.ChangedButton;
        _downScreen = screen;
        _downWorld = world;
        _lastMouseWorld = world;
        _changed = false;

        // Polyline / polygon in progress: the right button finishes it.
        if (_mode == Mode.Poly)
        {
            if (e.ChangedButton == MouseButton.Right) FinishPoly();
            else if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount >= 2) FinishPoly();
                else { _points.Add(world); UpdatePolyPreview(world); }
            }
            e.Handled = true;
            return;
        }

        // The right button opens the menu of the element under the mouse (when the button is released).
        if (e.ChangedButton == MouseButton.Right)
        {
            _menuTarget = HitElementLoose(screen);
            if (_menuTarget != null && !_vm.SelectedIds.Contains(_menuTarget.Id)) _vm.Select(new[] { _menuTarget.Id });
            e.Handled = true;
            return;
        }

        // Panning: middle button with any tool, or the Pan tool, or Space + drag.
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && (_vm.Tool == WbTool.Pan || _spaceDown)))
        {
            StartMode(Mode.Pan);
            _panStart = _offset;
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        switch (_vm.Tool)
        {
            case WbTool.Select:
                SelectToolDown(screen, e.ClickCount);
                break;

            case WbTool.Text:
            case WbTool.Note:
                CreateText(world, _vm.Tool == WbTool.Note);
                break;

            case WbTool.Pen:
                _points.Clear();
                _points.Add(world);
                StartMode(Mode.Pen);
                break;

            case WbTool.Line:
            case WbTool.Rectangle:
            case WbTool.Ellipse:
            case WbTool.Triangle:
                StartMode(Mode.Shape);
                break;

            case WbTool.Polyline:
            case WbTool.Polygon:
                _points.Clear();
                _points.Add(world);
                _mode = Mode.Poly;      // no mouse capture: the points are added click by click
                UpdatePolyPreview(world);
                break;

            case WbTool.Connector:
                ConnectorToolDown(screen);
                break;
        }
    }

    private MouseButton _downButton;   // the button of the last press that the canvas acted on

    private void StartMode(Mode mode)
    {
        _mode = mode;
        CaptureMouse();
    }

    private void SelectToolDown(Point screen, int clickCount)
    {
        var hit = HitTest(screen);
        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (hit)
        {
            case HandleTag handle:
                _active = _vm!.SelectedElements.FirstOrDefault();
                if (_active is null) return;
                _handleX = handle.X;
                _handleY = handle.Y;
                StartMode(handle.Rotate ? Mode.Rotate : Mode.Resize);
                return;

            case WbElement element:
                if (clickCount >= 2 && element.Kind == WbKind.Text)
                {
                    _vm!.Select(new[] { element.Id });
                    BeginEdit(element, isNew: false);
                    return;
                }
                if (additive) _vm!.ToggleSelect(element.Id);
                else if (!_vm!.SelectedIds.Contains(element.Id)) _vm.Select(new[] { element.Id });

                if (_vm.SelectedIds.Contains(element.Id))
                {
                    _moveOrigins = _vm.SelectedElements.ToDictionary(x => x.Id, x => new WbPoint(x.X, x.Y));
                    StartMode(Mode.Move);
                }
                return;

            case WbConnector connector:
                _vm!.SelectConnector(connector.Id);
                return;

            default:
                if (!additive) _vm!.ClearSelection();
                _marquee = new Rect(screen, screen);
                StartMode(Mode.Marquee);
                return;
        }
    }

    private void ConnectorToolDown(Point screen)
    {
        var hit = HitTest(screen);
        if (hit is WbConnector connector)
        {
            _pendingConnect = null;
            _vm!.SelectConnector(connector.Id);
            return;
        }
        var element = hit as WbElement ?? HitElementLoose(screen);
        if (element is null)
        {
            _pendingConnect = null;
            _vm!.ClearSelection();
            RefreshOverlay();
            return;
        }

        // Second click of a click-click connection.
        if (_pendingConnect != null && _pendingConnect != element)
        {
            _vm!.TryConnect(_pendingConnect.Id, element.Id);
            _pendingConnect = null;
            RefreshOverlay();
            return;
        }

        _connectFrom = element;
        StartMode(Mode.Connect);
        RefreshOverlay();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_vm is null) return;
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _lastMouseWorld = world;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_mode)
        {
            case Mode.Pan:
                _offset = _panStart + (screen - _downScreen);
                ApplyView();
                break;

            case Mode.Move:
            {
                var delta = world - _downWorld;
                if (!_changed)
                {
                    if ((screen - _downScreen).Length < 3) return;   // still a click, not a drag
                    _vm.Snapshot();
                    _changed = true;
                }
                foreach (var element in _vm.SelectedElements)
                {
                    if (!_moveOrigins.TryGetValue(element.Id, out var origin)) continue;
                    element.X = origin.X + delta.X;
                    element.Y = origin.Y + delta.Y;
                    if (_hosts.TryGetValue(element.Id, out var host)) PlaceVisual(host, element);
                }
                BuildConnectors();
                RefreshOverlay();
                break;
            }

            case Mode.Resize when _active != null:
            {
                if (!_changed) { _vm.Snapshot(); _changed = true; }
                // Images and groups keep their proportions from the corners; Shift inverts the behaviour.
                var proportional = _active.Kind is WbKind.Image or WbKind.Group;
                WhiteboardOps.Resize(_active, _handleX, _handleY, world, proportional != shift);
                RefreshElement(_active);
                if (_active.Kind == WbKind.Text) SyncTextHeights();
                BuildConnectors();
                RefreshOverlay();
                break;
            }

            case Mode.Rotate when _active != null:
            {
                if (!_changed) { _vm.Snapshot(); _changed = true; }
                var center = _active.Center;
                var angle = Math.Atan2(world.Y - center.Y, world.X - center.X) * 180 / Math.PI + 90;
                if (shift) angle = Math.Round(angle / 15) * 15;
                _active.Rotation = WhiteboardOps.NormalizeAngle(angle);
                if (_hosts.TryGetValue(_active.Id, out var host)) PlaceVisual(host, _active);
                BuildConnectors();
                RefreshOverlay();
                break;
            }

            case Mode.Marquee:
                _marquee = new Rect(_downScreen, screen);
                RefreshOverlay();
                break;

            case Mode.Shape:
                ShowPreview(ShapeFromDrag(_downWorld, world, shift));
                break;

            case Mode.Pen:
                if ((world - _points[^1]).Length * _zoom >= 1.5)
                {
                    _points.Add(world);
                    ShowPreview(PreviewStroke(_points, closed: false));
                }
                break;

            case Mode.Poly:
                UpdatePolyPreview(world);
                break;

            case Mode.Connect when _connectFrom != null:
            {
                _previewLayer.Children.Clear();
                var from = _connectFrom.Center;
                var line = new Line
                {
                    X1 = from.X, Y1 = from.Y, X2 = world.X, Y2 = world.Y,
                    StrokeThickness = Math.Max(1, _vm.ConnectorThickness), StrokeDashArray = new DoubleCollection { 4, 3 }
                };
                line.SetResourceReference(Shape.StrokeProperty, "Brush.Selection");
                _previewLayer.Children.Add(line);
                break;
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_vm != null && e.ChangedButton == MouseButton.Right && _mode == Mode.None)
        {
            var target = _menuTarget;
            _menuTarget = null;
            if (target is null) return;   // not ours: the text being edited keeps its own Cut / Copy / Paste menu
            if (_vm.SelectedIds.Contains(target.Id)) ShowElementMenu();
            e.Handled = true;
            return;
        }
        if (_vm is null || _mode is Mode.None or Mode.Poly) return;
        if (e.ChangedButton != _downButton) return;   // only the button that started the drag ends it

        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        _previewLayer.Children.Clear();

        switch (mode)
        {
            case Mode.Pan:
                OnToolCursor();
                break;

            case Mode.Move:
            case Mode.Resize:
            case Mode.Rotate:
                _active = null;
                if (_changed) _vm.Commit();
                else RefreshOverlay();
                break;

            case Mode.Marquee:
            {
                var rect = _marquee ?? new Rect();
                _marquee = null;
                if (rect.Width > 3 || rect.Height > 3)
                {
                    var a = ToWorld(rect.TopLeft);
                    var b = ToWorld(rect.BottomRight);
                    var area = new WbRect(a.X, a.Y, b.X, b.Y);
                    var ids = _vm.Data.Elements.Where(x => WhiteboardOps.Bounds(x).Intersects(area)).Select(x => x.Id).ToList();
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ids.AddRange(_vm.SelectedIds);
                    _vm.Select(ids.Distinct());
                }
                RefreshOverlay();
                break;
            }

            case Mode.Shape:
            {
                var element = ShapeFromDrag(_downWorld, world, shift);
                if (element.Width < 4 && element.Height < 4)
                {
                    // A plain click: place a shape of a default size.
                    element = ShapeFromDrag(_downWorld, new WbPoint(_downWorld.X + 140, _downWorld.Y + 90), false);
                }
                _vm.StyleNew(element);
                _vm.AddElement(element);
                _vm.Tool = WbTool.Select;
                break;
            }

            case Mode.Pen:
                FinishPen();
                break;

            case Mode.Connect:
            {
                var source = _connectFrom;
                _connectFrom = null;
                var target = HitElementLoose(screen);
                if (source != null && target != null && target != source)
                {
                    _vm.TryConnect(source.Id, target.Id);
                    _pendingConnect = null;
                }
                else if (source != null && target == source)
                {
                    _pendingConnect = source;   // click without drag: wait for a click on the second element
                    _vm.StatusText = "Now click the element to connect to.";
                }
                RefreshOverlay();
                break;
            }
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }

    private void OnToolCursor()
    {
        if (_vm is null) return;
        Cursor = _vm.Tool switch
        {
            WbTool.Select => Cursors.Arrow,
            WbTool.Pan => Cursors.SizeAll,
            WbTool.Text or WbTool.Note => Cursors.IBeam,
            WbTool.Connector => Cursors.Hand,
            _ => Cursors.Cross
        };
    }

    /// <summary>Abandons a drag / drawing in progress (Esc or tool change).</summary>
    private void CancelInteraction()
    {
        if (_mode == Mode.None && _previewLayer.Children.Count == 0) return;
        var hadChanges = _changed && _mode is Mode.Move or Mode.Resize or Mode.Rotate;
        _mode = Mode.None;
        _points.Clear();
        _marquee = null;
        _connectFrom = null;
        _active = null;
        _previewLayer.Children.Clear();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (hadChanges) _vm?.UndoCommand.Execute(null);   // put the elements back where they were
        _changed = false;
        OnToolCursor();
        RefreshOverlay();
    }

    // ================================================================== creating elements

    private WbElement ShapeFromDrag(WbPoint from, WbPoint to, bool shift)
    {
        var tool = _vm!.Tool;
        if (tool == WbTool.Line)
        {
            if (shift)
            {
                // Snap the direction to multiples of 15 degrees.
                var d = to - from;
                var angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 12)) * (Math.PI / 12);
                to = new WbPoint(from.X + Math.Cos(angle) * d.Length, from.Y + Math.Sin(angle) * d.Length);
            }
            return WhiteboardOps.FromPoints(WbKind.Line, new[] { from, to });
        }

        double w = Math.Abs(to.X - from.X), h = Math.Abs(to.Y - from.Y);
        if (shift) w = h = Math.Max(w, h);
        var x = to.X >= from.X ? from.X : from.X - w;
        var y = to.Y >= from.Y ? from.Y : from.Y - h;
        if (shift && tool == WbTool.Triangle) h = w * Math.Sqrt(3) / 2;   // equilateral
        return new WbElement
        {
            Kind = tool switch { WbTool.Ellipse => WbKind.Ellipse, WbTool.Triangle => WbKind.Triangle, _ => WbKind.Rectangle },
            X = x, Y = y, Width = w, Height = h
        };
    }

    private void ShowPreview(WbElement element)
    {
        _previewLayer.Children.Clear();
        _vm!.StyleNew(element);
        if (element.Width < 0.5 && element.Height < 0.5) return;
        var visual = BuildVisual(element);
        visual.Opacity = 0.85;
        _previewLayer.Children.Add(visual);
    }

    private static WbElement PreviewStroke(IReadOnlyList<WbPoint> points, bool closed)
    {
        var element = WhiteboardOps.FromPoints(WbKind.Stroke, points);
        element.Closed = closed;
        return element;
    }

    private void FinishPen()
    {
        var points = WhiteboardOps.Simplify(_points, 0.6 / _zoom);
        _points.Clear();
        if (points.Count < 2) return;

        // Ending close to where the stroke started makes a closed shape (which can be filled).
        var closed = points.Count >= 4 && (points[0] - points[^1]).Length * _zoom < 14;
        var element = WhiteboardOps.FromPoints(WbKind.Stroke, points);
        element.Closed = closed;
        _vm!.StyleNew(element);
        _vm.AddElement(element, select: false);   // keep drawing without selection frames in the way
    }

    private void UpdatePolyPreview(WbPoint mouse)
    {
        if (_points.Count == 0) return;
        var points = _points.Append(mouse).ToList();
        var element = WhiteboardOps.FromPoints(_vm!.Tool == WbTool.Polygon ? WbKind.Polygon : WbKind.Polyline, points);
        ShowPreview(element);
    }

    private void FinishPoly()
    {
        var isPolygon = _vm!.Tool == WbTool.Polygon;
        // Drop consecutive duplicates (a double-click adds the same point twice).
        var points = new List<WbPoint>();
        foreach (var p in _points)
            if (points.Count == 0 || (points[^1] - p).Length * _zoom > 2) points.Add(p);

        _points.Clear();
        _mode = Mode.None;
        _previewLayer.Children.Clear();
        if (points.Count < (isPolygon ? 3 : 2)) return;

        var element = WhiteboardOps.FromPoints(isPolygon ? WbKind.Polygon : WbKind.Polyline, points);
        _vm.StyleNew(element);
        _vm.AddElement(element);
        _vm.Tool = WbTool.Select;
    }

    private void CreateText(WbPoint at, bool note)
    {
        var element = new WbElement { Kind = WbKind.Text, X = at.X, Y = at.Y, Text = "" };
        _vm!.StyleNew(element);
        if (note)
        {
            element.Width = 180;
            element.Height = 130;
            element.Fill = WhiteboardViewModel.NoteColor;
            element.Stroke = null;          // text color chosen automatically to be readable on the note
            element.X -= 90;
            element.Y -= 20;
        }
        else
        {
            element.Width = 240;
            element.Height = element.FontSize * 1.35 + 12;
            element.Y -= element.Height / 2;
        }
        _vm.AddElement(element);
        _vm.Tool = WbTool.Select;
        BeginEdit(element, isNew: true);
    }

    // ================================================================== text editing

    private void BeginEdit(WbElement element, bool isNew)
    {
        if (!_hosts.TryGetValue(element.Id, out var host) || host is not Grid grid || grid.Children.Count == 0) return;
        if (grid.Children[0] is not Border border) return;

        var box = new TextBox
        {
            Text = element.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, -2, 0),
            FontSize = element.FontSize > 0 ? element.FontSize : 16,
            MinWidth = 20,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        SetTextColor(box, element);
        box.CaretBrush = box.Foreground;
        box.LostKeyboardFocus += (_, _) => EndEdit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                e.Handled = true;
                EndEdit();
                Focus();
            }
        };
        border.Child = box;

        _editing = element;
        _editBox = box;
        _editIsNew = isNew;
        RefreshOverlay();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Stores the edited text (an empty text removes the element).</summary>
    private void EndEdit()
    {
        if (_editing is null || _editBox is null || _endingEdit || _vm is null) return;
        _endingEdit = true;
        try
        {
            var element = _editing;
            var text = _editBox.Text.TrimEnd();
            var isNew = _editIsNew;
            _editing = null;
            _editBox = null;

            if (text.Length == 0)
            {
                if (isNew)
                {
                    // Nothing typed: as if the text box had never been created.
                    _vm.Data.Elements.Remove(element);
                    _vm.SelectedIds.Remove(element.Id);
                    _vm.DiscardSnapshot();
                }
                else
                {
                    _vm.Snapshot();
                    WhiteboardOps.Delete(_vm.Data, new[] { element.Id });
                }
                _vm.Commit();
            }
            else if (text != (element.Text ?? ""))
            {
                if (!isNew) _vm.Snapshot();
                element.Text = text;
                _vm.Commit();
            }
            else
            {
                SyncAll();
            }
        }
        finally
        {
            _endingEdit = false;
        }
    }

    /// <summary>The edited element disappeared (undo, board switch): just forget the text box.</summary>
    private void AbandonEdit()
    {
        _editing = null;
        _editBox = null;
    }

    // ================================================================== keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || _editing != null) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var handled = true;

        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                break;
            case Key.Delete:
            case Key.Back:
                _vm.DeleteCommand.Execute(null);
                break;
            case Key.Escape:
                if (_mode != Mode.None || _previewLayer.Children.Count > 0) CancelInteraction();
                else if (_pendingConnect != null) { _pendingConnect = null; RefreshOverlay(); }
                else if (_vm.HasSelection) _vm.ClearSelection();
                else _vm.Tool = WbTool.Select;
                break;
            case Key.Enter:
                if (_mode == Mode.Poly) FinishPoly();
                else handled = false;
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                _vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                _vm.UndoCommand.Execute(null);
                break;
            case Key.A when ctrl:
                _vm.SelectAllCommand.Execute(null);
                break;
            case Key.D when ctrl:
                _vm.DuplicateCommand.Execute(null);
                break;
            case Key.G when ctrl && shift:
                _vm.UngroupCommand.Execute(null);
                break;
            case Key.G when ctrl:
                _vm.GroupCommand.Execute(null);
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.X when ctrl:
                if (Copy()) _vm.DeleteCommand.Execute(null);
                break;
            case Key.V when ctrl:
                Paste();
                break;
            case Key.Left: Nudge(shift ? -10 : -1, 0); break;
            case Key.Right: Nudge(shift ? 10 : 1, 0); break;
            case Key.Up: Nudge(0, shift ? -10 : -1); break;
            case Key.Down: Nudge(0, shift ? 10 : 1); break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) _spaceDown = false;
    }

    private void Nudge(double dx, double dy)
    {
        if (_vm is null || _vm.SelectedIds.Count == 0) return;
        _vm.Snapshot();
        foreach (var element in _vm.SelectedElements)
        {
            element.X += dx;
            element.Y += dy;
        }
        _vm.Commit();
    }

    // ================================================================== clipboard, images, drag & drop

    private bool Copy()
    {
        var json = _vm?.CopySelection();
        if (json is null) return false;
        try
        {
            Clipboard.SetDataObject(new DataObject(ClipboardFormat, json), true);
            return true;
        }
        catch (Exception ex)
        {
            _vm!.StatusText = "Copy failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>Ctrl+V: whiteboard elements, a screenshot / image, image files, or plain text.</summary>
    private void Paste()
    {
        if (_vm is null) return;
        var at = IsMouseOver ? _lastMouseWorld : ViewCenter;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return;

            if (data.GetDataPresent(ClipboardFormat) && data.GetData(ClipboardFormat) is string json)
            {
                _vm.Paste(json, at);
                return;
            }

            // Many applications also offer the image as a PNG stream: it keeps transparency.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream png)
            {
                using var memory = new MemoryStream();
                png.CopyTo(memory);
                AddImageBytes(memory.ToArray(), at);
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap != null) { AddBitmap(bitmap, at); return; }
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (AddImageFiles(files, at) > 0) return;
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                var element = new WbElement { Kind = WbKind.Text, Text = text, Width = 280, X = at.X - 140, Y = at.Y - 20 };
                _vm.StyleNew(element);
                element.Height = element.FontSize * 1.35 + 12;
                _vm.AddElement(element);
                return;
            }

            _vm.StatusText = "Nothing to paste: copy an image, a screenshot, text or whiteboard elements first.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Paste failed: " + ex.Message;
        }
    }

    /// <summary>Clipboard bitmaps (screenshots) often carry an empty alpha channel: drop it, then store as PNG.</summary>
    private void AddBitmap(BitmapSource bitmap, WbPoint center)
    {
        var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        AddImageBytes(memory.ToArray(), center);
    }

    private void AddImageBytes(byte[] bytes, WbPoint center)
    {
        var file = _vm!.Service.SaveImage(bytes);
        AddImageElement(file, center);
    }

    private int AddImageFiles(IEnumerable<string> files, WbPoint center)
    {
        var added = 0;
        foreach (var path in files)
        {
            if (!File.Exists(path) || !WhiteboardService.IsImageFile(path)) continue;
            var file = _vm!.Service.ImportImage(path);
            AddImageElement(file, new WbPoint(center.X + added * 30, center.Y + added * 30));
            added++;
        }
        if (added == 0) _vm!.StatusText = "Only image files (png, jpg, bmp, gif, tif, webp) can be added.";
        return added;
    }

    private void AddImageElement(string file, WbPoint center)
    {
        var source = LoadImage(file);
        if (source is null)
        {
            _vm!.StatusText = "The image could not be read.";
            return;
        }
        // Start at the pixel size, but never huge on the board.
        double w = source.PixelWidth, h = source.PixelHeight;
        var scale = Math.Min(1, Math.Min(640 / w, 480 / h));
        w *= scale;
        h *= scale;
        var element = new WbElement
        {
            Kind = WbKind.Image, Image = file, Width = w, Height = h, X = center.X - w / 2, Y = center.Y - h / 2, StrokeWidth = 0
        };
        _vm!.AddElement(element);
        _vm.Tool = WbTool.Select;
        _vm.StatusText = "Image added.";
    }

    /// <summary>
    /// A "virtual file" dropped by another application (browsers do this when an image is dragged
    /// from a web page): accept it only if the bytes really are an image, and store it as PNG.
    /// </summary>
    private void AddVirtualFile(byte[] bytes, WbPoint center)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using var output = new MemoryStream();
            encoder.Save(output);
            AddImageBytes(output.ToArray(), center);
        }
        catch (Exception)
        {
            _vm!.StatusText = "The dropped item is not an image that can be read.";
        }
    }

    private void AddImageFromDialog()
    {
        var path = _vm?.Dialogs.PickFile("Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*");
        if (path is null) return;
        try { AddImageFiles(new[] { path }, ViewCenter); }
        catch (Exception ex) { _vm!.StatusText = "The image could not be added: " + ex.Message; }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ||
                    e.Data.GetDataPresent("FileContents")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (_vm is null) return;
        e.Handled = true;
        var at = ToWorld(e.GetPosition(this));
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
                AddImageFiles(files, at);
            else if (e.Data.GetDataPresent("FileContents") && e.Data.GetData("FileContents") is MemoryStream contents)
                AddVirtualFile(contents.ToArray(), at);   // image dragged from a web browser
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                AddBitmap(bitmap, at);
            else
                _vm.StatusText = "Only images can be dropped on the whiteboard.";
            Focus();
        }
        catch (Exception ex)
        {
            _vm.StatusText = "The image could not be added: " + ex.Message;
        }
    }

    // ================================================================== export

    /// <summary>Saves the whole board as a PNG picture (with the current theme background).</summary>
    private void ExportPng()
    {
        if (_vm is null) return;
        if (_vm.Data.Elements.Count == 0)
        {
            _vm.StatusText = "The whiteboard is empty: nothing to export.";
            return;
        }
        var path = _vm.Dialogs.PickSaveFile(_vm.Data.Name + ".png", "PNG image|*.png");
        if (path is null) return;

        try
        {
            const double margin = 32, scale = 2;
            var bounds = WhiteboardOps.Bounds(_vm.Data.Elements);
            var width = bounds.Width + 2 * margin;
            var height = bounds.Height + 2 * margin;

            // Draw a fresh copy of the board (without selection frames) on an off-screen canvas.
            var root = new Canvas { Width = width, Height = height };
            root.SetResourceReference(BackgroundProperty, "Brush.Canvas");
            var content = new Canvas { RenderTransform = new TranslateTransform(margin - bounds.Left, margin - bounds.Top) };
            root.Children.Add(content);

            var connectors = new Canvas();
            content.Children.Add(connectors);
            foreach (var element in _vm.Data.Elements) content.Children.Add(BuildVisual(element));
            foreach (var child in ConnectorVisualsForExport()) connectors.Children.Add(child);

            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            _vm.StatusText = "Exported to " + path;
        }
        catch (Exception ex)
        {
            _vm.Dialogs.Error("The export failed.\n\n" + ex.Message);
        }
    }

    private List<UIElement> ConnectorVisualsForExport()
    {
        // Reuse the normal connector drawing, minus the invisible hit lines.
        BuildConnectors();
        var visuals = _connectorLayer.Children.Cast<UIElement>().Where(c => c.IsHitTestVisible == false).ToList();
        _connectorLayer.Children.Clear();
        Dispatcher.BeginInvoke(new Action(BuildConnectors));
        return visuals;
    }
}
