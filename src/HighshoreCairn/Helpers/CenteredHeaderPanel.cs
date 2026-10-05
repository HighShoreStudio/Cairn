using System.Windows;
using System.Windows.Controls;

namespace HighshoreCairn.Helpers;

/// <summary>
/// A header with three children: the first on the left, the third on the right and the second one in
/// the middle of the whole width (not of the space left between the other two). An optional fourth
/// child (a notice) fills the room between the middle and the right child and is the first to shorten.
/// When the middle child would cover a neighbour it slides just enough to stay clear; when there is
/// no room at all the left child shrinks down to <see cref="LeftMinWidth"/>.
/// </summary>
public class CenteredHeaderPanel : Panel
{
    /// <summary>Minimum distance between the children.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CenteredHeaderPanel),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width kept for the left child when everything does not fit.</summary>
    public double LeftMinWidth
    {
        get => (double)GetValue(LeftMinWidthProperty);
        set => SetValue(LeftMinWidthProperty, value);
    }

    public static readonly DependencyProperty LeftMinWidthProperty = DependencyProperty.Register(
        nameof(LeftMinWidth), typeof(double), typeof(CenteredHeaderPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width the notice keeps: the middle child leaves its centered place before the notice gets narrower than this.</summary>
    public double NoticeMinWidth
    {
        get => (double)GetValue(NoticeMinWidthProperty);
        set => SetValue(NoticeMinWidthProperty, value);
    }

    public static readonly DependencyProperty NoticeMinWidthProperty = DependencyProperty.Register(
        nameof(NoticeMinWidth), typeof(double), typeof(CenteredHeaderPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private UIElement? Child(int index) => InternalChildren.Count > index ? InternalChildren[index] : null;

    private static bool Shown(UIElement? element) => element != null && element.Visibility != Visibility.Collapsed;

    private readonly record struct Slots(double LeftWidth, double CenterX, double CenterWidth,
        double NoticeX, double NoticeWidth, double RightX, double RightWidth);

    /// <summary>Places the four parts in <paramref name="width"/>, given the width each would like to have.</summary>
    private Slots Place(double width, double left, double center, double notice, double right, bool hasCenter, bool hasNotice, bool hasRight)
    {
        right = Math.Min(right, width);
        var rightX = width - right;
        var end = rightX - (hasRight ? Gap : 0);                       // where the left / middle / notice area ends
        var noticeMin = hasNotice ? Math.Min(notice, NoticeMinWidth) : 0;
        var noticeRoom = hasNotice && noticeMin > 0 ? noticeMin + Gap : 0;

        double x, centerWidth = hasCenter ? center : 0;
        if (hasCenter)
        {
            var ideal = (width - centerWidth) / 2;
            var most = end - centerWidth - noticeRoom;                 // right-most place that keeps the notice readable
            var least = left + Gap;                                    // left child at its full width
            if (most >= least) x = Math.Clamp(ideal, least, most);
            else
            {
                var floor = Math.Min(left, LeftMinWidth) + Gap;        // the left child shortens first...
                x = most >= floor ? most : Math.Min(floor, end - centerWidth);   // ...then the notice gives way
            }
            x = Math.Max(0, x);
            left = Math.Max(0, Math.Min(left, x - Gap));
        }
        else
        {
            left = Math.Max(0, Math.Min(left, end - noticeRoom));
            x = left;
        }

        var from = hasCenter ? x + centerWidth + Gap : left + (left > 0 ? Gap : 0);
        var noticeWidth = hasNotice ? Math.Clamp(end - from, 0, notice) : 0;
        return new Slots(left, x, centerWidth, end - noticeWidth, noticeWidth, rightX, right);
    }

    // Widths the children asked for with no limit, kept from the last measure pass: the two that shorten
    // report the shortened width afterwards, and measuring again while arranging would restart the layout.
    private double _leftWanted, _centerWanted, _noticeWanted, _rightWanted;

    protected override Size MeasureOverride(Size available)
    {
        var (left, center, right, notice) = (Child(0), Child(1), Child(2), Child(3));
        var infinite = new Size(double.PositiveInfinity, available.Height);
        foreach (UIElement child in InternalChildren) child.Measure(infinite);

        static double Wanted(UIElement? element) => Shown(element) ? element!.DesiredSize.Width : 0;
        (_leftWanted, _centerWanted, _noticeWanted, _rightWanted) = (Wanted(left), Wanted(center), Wanted(notice), Wanted(right));
        var natural = _leftWanted + _centerWanted + _noticeWanted + _rightWanted
                      + Gap * Math.Max(0, new[] { left, center, notice, right }.Count(Shown) - 1);

        if (!double.IsInfinity(available.Width))
        {
            var slots = Place(available.Width, _leftWanted, _centerWanted, _noticeWanted, _rightWanted,
                Shown(center), Shown(notice), Shown(right));
            // the ones that can shorten are measured again with the width they really get
            if (Shown(left) && slots.LeftWidth < _leftWanted) left!.Measure(new Size(slots.LeftWidth, available.Height));
            if (Shown(notice) && slots.NoticeWidth < _noticeWanted) notice!.Measure(new Size(slots.NoticeWidth, available.Height));
            if (Shown(right) && slots.RightWidth < _rightWanted) right!.Measure(new Size(slots.RightWidth, available.Height));
        }

        var height = 0d;
        foreach (UIElement child in InternalChildren) height = Math.Max(height, child.DesiredSize.Height);
        return new Size(double.IsInfinity(available.Width) ? natural : Math.Min(natural, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (left, center, right, notice) = (Child(0), Child(1), Child(2), Child(3));
        var slots = Place(final.Width, _leftWanted, _centerWanted, _noticeWanted, _rightWanted,
            Shown(center), Shown(notice), Shown(right));

        left?.Arrange(new Rect(0, 0, slots.LeftWidth, final.Height));
        center?.Arrange(new Rect(slots.CenterX, 0, slots.CenterWidth, final.Height));
        notice?.Arrange(new Rect(slots.NoticeX, 0, slots.NoticeWidth, final.Height));
        right?.Arrange(new Rect(slots.RightX, 0, slots.RightWidth, final.Height));
        return final;
    }
}
