using System.Windows;

namespace HighshoreCairn.Views;

public partial class AccountDialog : Window
{
    public AccountDialog()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UsernameBox.Focus();
}
