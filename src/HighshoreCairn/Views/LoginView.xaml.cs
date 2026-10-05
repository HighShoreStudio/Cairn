using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UsernameBox.Focus();

    // Enter submits the form (login or create, depending on the mode).
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is ButtonBase) return;
        if (DataContext is LoginViewModel vm)
        {
            vm.Submit();
            e.Handled = true;
        }
    }
}
