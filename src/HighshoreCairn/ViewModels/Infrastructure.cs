using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HighshoreCairn.ViewModels;

/// <summary>Base class of every ViewModel: implements INotifyPropertyChanged for data binding.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Tells the view to re-read every bound property.</summary>
    public void RaiseAllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>ICommand that runs a delegate: lets XAML buttons call ViewModel methods.</summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Typed variant: the command parameter is cast to T.</summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    public RelayCommand(Action<T?> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter is T value ? value : default);
}

/// <summary>Base class of the ViewModels shown in a modal dialog window.</summary>
public abstract class DialogViewModel : ObservableObject
{
    /// <summary>Set by the dialog service: closes the window with the given result.</summary>
    public Action<bool>? CloseRequested { get; set; }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    protected void Close(bool result) => CloseRequested?.Invoke(result);
}

/// <summary>
/// Everything the ViewModels need from the UI (windows, message boxes, pickers).
/// The implementation lives in the View layer, so ViewModels never reference WPF windows.
/// </summary>
public interface IDialogService
{
    bool? ShowDialog(DialogViewModel viewModel);
    bool Confirm(string message, string title = "Confirm");
    void Info(string message, string title = "Cairn");
    void Error(string message, string title = "Error");
    string? PickFolder(string? initialFolder = null);
    void OpenFolder(string folder);
    /// <summary>Open-file dialog. <paramref name="filter"/> uses the Win32 syntax ("Images|*.png;*.jpg").</summary>
    string? PickFile(string filter);
    /// <summary>Save-file dialog. Returns null when cancelled.</summary>
    string? PickSaveFile(string defaultName, string filter);
    /// <summary>Open-file dialog with multiple selection. Empty when cancelled.</summary>
    IReadOnlyList<string> PickFiles(string filter);
    /// <summary>
    /// Lets the user pick a picture and returns it as a small square PNG encoded in base64
    /// (the picture is cropped around its center). null when cancelled.
    /// </summary>
    string? PickSquareImage(int size = 128);
    /// <summary>Yes / No / Cancel question: true, false or null.</summary>
    bool? Ask(string message, string title);
    /// <summary>Opens a file or a web link with the default application of Windows.</summary>
    void OpenExternal(string target);
    /// <summary>
    /// Shows a window that stays open next to the main one (not modal); showing the same ViewModel
    /// again brings its window to the front. The window closes when the ViewModel asks for it.
    /// </summary>
    void ShowWindow(WindowViewModel viewModel);
}

/// <summary>Base class of the ViewModels shown in a window of their own that is not a dialog.</summary>
public abstract class WindowViewModel : ObservableObject
{
    /// <summary>Raised by the ViewModel to close its window.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised to bring the window to the front.</summary>
    public event Action? ActivateRequested;

    public bool IsOpen { get; private set; }

    /// <summary>Called by the dialog service when the window opens / has been closed.</summary>
    public virtual void OnOpened() => IsOpen = true;
    public virtual void OnClosed() => IsOpen = false;

    public void RequestClose() => CloseRequested?.Invoke();
    public void RequestActivate() => ActivateRequested?.Invoke();
}

/// <summary>Plays the short notification sounds (implemented in the View layer).</summary>
public interface ISoundService
{
    /// <param name="sound">"None", "Chime", "Bell", "Digital" or "Custom".</param>
    /// <param name="volume">0-100.</param>
    /// <param name="customFile">A .wav file, used when <paramref name="sound"/> is "Custom".</param>
    void Play(string sound, int volume, string? customFile = null);
}

/// <summary>
/// Converts documents between markdown and rich text. Implemented in the View layer, because the
/// rich text format is read and written by the WPF text engine.
/// </summary>
public interface IDocConverter
{
    /// <param name="resolveImage">Maps the source of a picture of the document to a file (null = not found).</param>
    string MarkdownToRtf(string markdown, Func<string, string?> resolveImage);

    /// <param name="saveImage">Stores a picture found in the rich text (PNG bytes) and returns the link to write.</param>
    string RtfToMarkdown(string rtf, Func<byte[], string> saveImage);
}

/// <summary>Switches between the light and the dark theme (implemented in the View layer).</summary>
public interface IThemeService
{
    bool IsDark { get; }
    void Apply(bool dark);
}

/// <summary>Small text / password prompt.</summary>
public class PromptViewModel : DialogViewModel
{
    public PromptViewModel(string title, string message, string initial = "", bool isPassword = false)
    {
        Title = title;
        Message = message;
        _value = initial;
        IsPassword = isPassword;
        OkCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string Title { get; }
    public string Message { get; }
    public bool IsPassword { get; }
    public bool IsText => !IsPassword;

    private string _value;
    public string Value { get => _value; set => SetProperty(ref _value, value); }

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }
}

public static class DialogServiceExtensions
{
    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    public static string? Prompt(this IDialogService dialogs, string title, string message, string initial = "")
    {
        var vm = new PromptViewModel(title, message, initial);
        return dialogs.ShowDialog(vm) == true ? vm.Value?.Trim() : null;
    }

    /// <summary>Asks for a password. Returns null when cancelled.</summary>
    public static string? PromptPassword(this IDialogService dialogs, string title, string message)
    {
        var vm = new PromptViewModel(title, message, "", isPassword: true);
        return dialogs.ShowDialog(vm) == true ? vm.Value : null;
    }
}

/// <summary>Helpers for text shown inside WPF menus.</summary>
public static class MenuText
{
    /// <summary>In a MenuItem header "_" marks an access key: double it to show it literally.</summary>
    public static string Escape(string? text) => (text ?? "").Replace("_", "__");
}

/// <summary>Colors offered by the avatar / tag / project color pickers.</summary>
public static class Palette
{
    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#1F6F8B", "#16324F", "#2E86AB", "#3E9A8E", "#30A46C", "#7CB518",
        "#F2A541", "#F76B15", "#E5484D", "#D6409F", "#8E4EC6", "#5B5BD6",
        "#0091FF", "#12A594", "#8D8D8D", "#5F6B7A"
    };

    public static string Random() => Colors[System.Random.Shared.Next(Colors.Count)];

    public static string PriorityColor(Models.Priority priority) => priority switch
    {
        Models.Priority.Low => "#3E9A8E",
        Models.Priority.Medium => "#2E86AB",
        Models.Priority.High => "#F2A541",
        Models.Priority.Urgent => "#E5484D",
        _ => "#C5CCD3"
    };

    /// <summary>Accepts "#RGB", "#RRGGBB" or "#AARRGGBB".</summary>
    public static bool IsValidHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#') return false;
        var hex = value[1..];
        return (hex.Length is 3 or 6 or 8) && hex.All(Uri.IsHexDigit);
    }
}
