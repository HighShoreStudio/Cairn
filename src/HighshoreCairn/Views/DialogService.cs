using System.Diagnostics;
using System.Windows;
using HighshoreCairn.ViewModels;
using Microsoft.Win32;

namespace HighshoreCairn.Views;

/// <summary>WPF implementation of <see cref="IDialogService"/>: maps each dialog ViewModel to its window.</summary>
public class DialogService : IDialogService
{
    private static Window? ActiveWindow =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;

    public bool? ShowDialog(DialogViewModel viewModel)
    {
        Window window = viewModel switch
        {
            CardEditorViewModel => new CardDialog(),
            PromptViewModel => new PromptDialog(),
            AccountEditorViewModel => new AccountDialog(),
            AccountManagerViewModel => new AccountManagerDialog(),
            ProjectEditorViewModel => new ProjectDialog(),
            ProjectSettingsViewModel => new ProjectSettingsDialog(),
            ProjectAccessViewModel => new ProjectAccessDialog(),
            TagManagerViewModel => new TagManagerDialog(),
            ArchiveViewModel => new ArchiveDialog(),
            ColumnStyleViewModel => new ColumnStyleDialog(),
            NewDocumentViewModel => new NewDocumentDialog(),
            DocPickerViewModel => new DocPickerDialog(),
            ItemPickerViewModel => new ItemPickerDialog(),
            MilestoneEditorViewModel => new MilestoneDialog(),
            RoadmapItemEditorViewModel => new RoadmapItemDialog(),
            IconPickerViewModel => new IconPickerDialog(),
            _ => throw new NotSupportedException($"No dialog registered for {viewModel.GetType().Name}.")
        };

        var owner = ActiveWindow;
        if (owner != null && owner != window && owner.IsVisible) window.Owner = owner;
        window.WindowStartupLocation = window.Owner != null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;
        window.DataContext = viewModel;
        ThemeService.Track(window);
        viewModel.CloseRequested = result => window.DialogResult = result;
        try
        {
            return window.ShowDialog();
        }
        finally
        {
            viewModel.CloseRequested = null;
        }
    }

    private readonly Dictionary<WindowViewModel, Window> _windows = new();

    /// <summary>
    /// Opens a window that lives next to the main one (the roadmap). It is not owned by the main window,
    /// so it can go behind it or on another screen; the dialogs it opens stay in front of it.
    /// </summary>
    public void ShowWindow(WindowViewModel viewModel)
    {
        if (_windows.TryGetValue(viewModel, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        Window window = viewModel switch
        {
            RoadmapViewModel => new RoadmapWindow(),
            _ => throw new NotSupportedException($"No window registered for {viewModel.GetType().Name}.")
        };
        window.DataContext = viewModel;
        ThemeService.Track(window);
        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            // Start over the main window, a little smaller.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, main.ActualWidth - 60));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, main.ActualHeight - 60));
            window.Left = main.Left + Math.Max(0, (main.ActualWidth - window.Width) / 2);
            window.Top = main.Top + Math.Max(0, (main.ActualHeight - window.Height) / 2);
            if (main.WindowState == WindowState.Maximized) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void Close() => window.Close();
        void Activate()
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        viewModel.CloseRequested += Close;
        viewModel.ActivateRequested += Activate;
        window.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= Close;
            viewModel.ActivateRequested -= Activate;
            _windows.Remove(viewModel);
            viewModel.OnClosed();
        };
        _windows[viewModel] = window;
        viewModel.OnOpened();
        window.Show();
    }

    public bool Confirm(string message, string title = "Confirm") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message, string title = "Cairn") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title = "Error") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = ActiveWindow;
        return owner != null && owner.IsVisible
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }

    public string? PickFolder(string? initialFolder = null)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder" };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;
        var owner = ActiveWindow;
        var picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FolderName : null;
    }

    public string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        var owner = ActiveWindow;
        var picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultName, Filter = filter, OverwritePrompt = true };
        var owner = ActiveWindow;
        var picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickFiles(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = true };
        var owner = ActiveWindow;
        var picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickSquareImage(int size = 128)
    {
        var file = PickFile("Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*");
        if (file is null) return null;
        try
        {
            return Helpers.ImageCodec.SquarePngBase64(file, size);
        }
        catch (Exception ex)
        {
            Error("The picture could not be read.\n\n" + ex.Message);
            return null;
        }
    }

    public bool? Ask(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null
        };

    public void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("It could not be opened.\n\n" + ex.Message);
        }
    }

    public void OpenFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("The folder could not be opened.\n\n" + ex.Message);
        }
    }
}
