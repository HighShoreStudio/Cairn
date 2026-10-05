using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

public partial class ProjectListView : UserControl
{
    private Window? _window;

    public ProjectListView()
    {
        InitializeComponent();
        // The "Trash Folder" button follows what happens in Explorer while the app is in the background.
        Loaded += (_, _) =>
        {
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = Window.GetWindow(this);
            if (_window != null) _window.Activated += OnWindowActivated;
            (DataContext as ProjectListViewModel)?.RefreshTrash();
        };
        Unloaded += (_, _) =>
        {
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
    }

    private void OnWindowActivated(object? sender, EventArgs e) => (DataContext as ProjectListViewModel)?.RefreshTrash();

    private void OpenSelected()
    {
        if (DataContext is ProjectListViewModel { Selected: not null } vm)
            vm.OpenCommand.Execute(vm.Selected);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the empty area or on the scrollbar.
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ProjectsList, source) is ListBoxItem)
            OpenSelected();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }
}
