using System.Windows;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class PromptDialog : Window
{
    public PromptDialog()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PromptViewModel { IsPassword: true })
        {
            PasswordInput.Focus();
        }
        else
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        }
    }
}
