using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HighshoreCairn.Helpers;

/// <summary>
/// Small pictures (profile pictures, project icons) are stored inside the JSON files as base64 PNG text,
/// so they travel with the project. This class converts between files, base64 and WPF images.
/// </summary>
public static class ImageCodec
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>Decodes a base64 picture. Returns null when the text is empty or not a valid image.</summary>
    public static ImageSource? FromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (Cache.TryGetValue(base64, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            bitmap.EndInit();
            bitmap.Freeze();
            result = bitmap;
        }
        catch
        {
            // not an image: the caller falls back to the colored initials
        }

        if (Cache.Count > 300) Cache.Clear();
        Cache[base64] = result;
        return result;
    }

    /// <summary>
    /// Loads a picture file, crops it to a square around its center, scales it down to
    /// <paramref name="size"/> pixels and returns it as base64 PNG.
    /// </summary>
    public static string SquarePngBase64(string file, int size)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        source.UriSource = new Uri(file, UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        if (side <= 0) throw new InvalidOperationException("The picture is empty.");
        var crop = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var target = Math.Min(size, side);

        // Draw with high quality scaling (a plain TransformedBitmap gives jagged thumbnails).
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
            context.DrawImage(crop, new Rect(0, 0, target, target));
        var bitmap = new RenderTargetBitmap(target, target, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>Loads an image file without keeping it locked (null when it cannot be read).</summary>
    public static ImageSource? FromFile(string? file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(file, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>base64 PNG text -> ImageSource, for Image elements bound to a ViewModel.</summary>
public class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ImageCodec.FromBase64(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>File path -> ImageSource (the file is not kept open).</summary>
public class FileToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ImageCodec.FromFile(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
