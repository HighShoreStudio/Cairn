using System.ComponentModel;
using System.Windows;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class CardDialog : Window
{
    public CardDialog()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    // Closing without Save (Cancel, Esc, X): ask before discarding changes.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true && DataContext is CardEditorViewModel vm && !vm.ConfirmDiscard())
            e.Cancel = true;
        base.OnClosing(e);
    }
}
