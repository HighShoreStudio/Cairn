using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HighshoreCairn.Views;

public partial class RoadmapWindow : Window
{
    public RoadmapWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Chart.Focus();
        // Clicking anywhere that is not a text box gives the keys back to the chart (arrows, Del, Enter).
        PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source || FindParent<TextBox>(source) != null) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!IsKeyboardFocusWithin || Keyboard.FocusedElement is not TextBox) Chart.Focus(); }));
        };
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
