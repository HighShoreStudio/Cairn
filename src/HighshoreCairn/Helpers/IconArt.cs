using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HighshoreCairn.Services;

namespace HighshoreCairn.Helpers;

/// <summary>
/// Draws icons given as "ion:name" (the built-in Ionicons set, vector) or "file:name" (a picture or an
/// SVG file in a folder of the project). Vector icons are painted with the brush that is passed in,
/// so they follow the theme and the color of what they sit on.
/// </summary>
public static class IconArt
{
    private static IconLibrary? _library;

    /// <summary>The icon set embedded in the application (Assets/ionicons.zip).</summary>
    public static IconLibrary Library
    {
        get
        {
            if (_library != null) return _library;
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ionicons.zip");
                _library = new IconLibrary(stream);
            }
            catch
            {
                _library = new IconLibrary(null);
            }
            return _library;
        }
    }

    private sealed class Vector
    {
        public double Width, Height;
        public List<(Geometry Geometry, SvgShape Shape)> Parts = new();
    }

    private static readonly Dictionary<string, Vector?> Vectors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime Stamp, object? Content)> Files = new(StringComparer.OrdinalIgnoreCase);

    private static Vector? ToVector(SvgIcon? icon)
    {
        if (icon is null) return null;
        var vector = new Vector { Width = icon.Width, Height = icon.Height };
        foreach (var shape in icon.Shapes)
        {
            try
            {
                var geometry = Geometry.Parse((shape.EvenOdd ? "F0 " : "F1 ") + shape.Path);
                geometry.Freeze();
                vector.Parts.Add((geometry, shape));
            }
            catch
            {
                // a shape WPF cannot read is left out: the rest of the icon is still drawn
            }
        }
        return vector.Parts.Count > 0 ? vector : null;
    }

    private static Vector? BuiltIn(string name)
    {
        lock (Vectors)
        {
            if (Vectors.TryGetValue(name, out var cached)) return cached;
            var vector = ToVector(Library.Get(name));
            Vectors[name] = vector;
            return vector;
        }
    }

    /// <summary>A picture (ImageSource) or a vector (Vector) loaded from a file; reloaded when the file changes.</summary>
    private static object? FromFile(string path)
    {
        DateTime stamp;
        try
        {
            if (!File.Exists(path)) return null;
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch { return null; }

        lock (Files)
        {
            if (Files.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Content;
            object? content = null;
            try
            {
                if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    content = ToVector(SvgParser.Parse(File.ReadAllText(path)));
                }
                else
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                    bitmap.EndInit();
                    bitmap.Freeze();
                    content = bitmap;
                }
            }
            catch
            {
                content = null;
            }
            if (Files.Count > 200) Files.Clear();
            Files[path] = (stamp, content);
            return content;
        }
    }

    /// <summary>True when the icon can be drawn (it exists and is readable).</summary>
    public static bool Exists(string? icon, string? folder) => Resolve(icon, folder) != null;

    private static object? Resolve(string? icon, string? folder)
    {
        if (IconLibrary.BuiltInName(icon) is { } name) return BuiltIn(name);
        if (IconLibrary.FileName(icon) is { } file && !string.IsNullOrEmpty(folder))
            return FromFile(Path.Combine(folder, Path.GetFileName(file)));
        return null;
    }

    /// <summary>
    /// Draws the icon inside <paramref name="box"/>, keeping its proportions. When the icon cannot be
    /// found the default one (a flag) is drawn, so a missing file never leaves a hole.
    /// </summary>
    public static void Draw(DrawingContext dc, string? icon, string? folder, Rect box, Brush brush)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var content = Resolve(icon, folder) ?? Resolve(Models.MilestoneType.DefaultIcon, null);
        switch (content)
        {
            case ImageSource image:
            {
                var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
                var size = new Size(image.Width * scale, image.Height * scale);
                dc.DrawImage(image, new Rect(box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2, size.Width, size.Height));
                break;
            }
            case Vector vector:
            {
                var scale = Math.Min(box.Width / vector.Width, box.Height / vector.Height);
                var x = box.X + (box.Width - vector.Width * scale) / 2;
                var y = box.Y + (box.Height - vector.Height * scale) / 2;
                dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
                foreach (var (geometry, shape) in vector.Parts)
                {
                    Pen? pen = null;
                    if (shape.Stroke)
                    {
                        pen = new Pen(brush, shape.StrokeWidth)
                        {
                            StartLineCap = shape.RoundCap ? PenLineCap.Round : PenLineCap.Flat,
                            EndLineCap = shape.RoundCap ? PenLineCap.Round : PenLineCap.Flat,
                            LineJoin = shape.RoundJoin ? PenLineJoin.Round : PenLineJoin.Miter
                        };
                    }
                    dc.DrawGeometry(shape.Fill ? brush : null, pen, geometry);
                }
                dc.Pop();
                break;
            }
        }
    }
}

/// <summary>Shows an icon ("ion:name" or "file:name") in XAML.</summary>
public class IconView : FrameworkElement
{
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(IconView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Folder of the "file:" icons (the roadmap-icons folder of the project).</summary>
    public string? Folder
    {
        get => (string?)GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    public static readonly DependencyProperty FolderProperty = DependencyProperty.Register(
        nameof(Folder), typeof(string), typeof(IconView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(IconView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent background makes the whole box clickable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        IconArt.Draw(dc, Icon, Folder, new Rect(0, 0, ActualWidth, ActualHeight), Foreground);
    }
}
