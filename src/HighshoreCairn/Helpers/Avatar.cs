using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HighshoreCairn.Converters;

namespace HighshoreCairn.Helpers;

/// <summary>
/// Round colored badge with the user's initials, or the user's picture when one is set
/// (a square picture, shown with slightly rounded corners).
/// XAML: &lt;h:Avatar Initials="{Binding Initials}" Color="{Binding AvatarColor}" Image="{Binding AvatarImage}" Size="24" /&gt;
/// </summary>
public class Avatar : Border
{
    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static readonly DependencyProperty InitialsProperty = DependencyProperty.Register(
        nameof(Initials), typeof(string), typeof(Avatar), new PropertyMetadata("?", OnChanged));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(Avatar), new PropertyMetadata("#8D8D8D", OnChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(28.0, OnChanged));

    /// <summary>Optional picture as base64 PNG (see <see cref="ImageCodec"/>). Empty = show the initials.</summary>
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(string), typeof(Avatar), new PropertyMetadata(null, OnChanged));

    public string? Image
    {
        get => (string?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>Rounded square instead of a circle (used for project icons).</summary>
    public static readonly DependencyProperty SquareProperty = DependencyProperty.Register(
        nameof(Square), typeof(bool), typeof(Avatar), new PropertyMetadata(false, OnChanged));

    public bool Square
    {
        get => (bool)GetValue(SquareProperty);
        set => SetValue(SquareProperty, value);
    }

    public Avatar()
    {
        Child = _text;
        SnapsToDevicePixels = true;
        Refresh();
    }

    public string Initials
    {
        get => (string)GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    /// <summary>Hex color, e.g. "#1F6F8B".</summary>
    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Avatar)d).Refresh();

    private void Refresh()
    {
        Width = Size;
        Height = Size;
        _text.Text = Initials;
        _text.FontSize = Math.Max(8, Size * 0.4);

        var picture = ImageCodec.FromBase64(Image);
        if (picture != null)
        {
            var brush = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            Background = brush;
            CornerRadius = new CornerRadius(Math.Max(2, Size * 0.2));
            _text.Visibility = Visibility.Collapsed;
        }
        else
        {
            Background = HexToBrushConverter.Parse(Color);
            CornerRadius = new CornerRadius(Square ? Math.Max(2, Size * 0.2) : Size / 2);
            _text.Visibility = Visibility.Visible;
        }
    }
}
