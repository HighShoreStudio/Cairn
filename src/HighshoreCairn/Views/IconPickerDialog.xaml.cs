using System.Windows;
using System.Windows.Input;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class IconPickerDialog : Window
{
    public IconPickerDialog() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is IconPickerViewModel vm) vm.OkCommand.Execute(null);
    }
}
