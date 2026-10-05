using System.Windows;
using System.Windows.Input;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class DocPickerDialog : Window
{
    public DocPickerDialog()
    {
        InitializeComponent();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DocPickerViewModel vm) vm.OkCommand.Execute(null);
    }

    /// <summary>Arrow keys move the selection while the caret stays in the filter box.</summary>
    private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down && e.Key != Key.Up) return;
        var index = List.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
        if (index >= 0 && index < List.Items.Count)
        {
            List.SelectedIndex = index;
            List.ScrollIntoView(List.SelectedItem);
        }
        e.Handled = true;
    }
}
