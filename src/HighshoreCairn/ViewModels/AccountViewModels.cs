using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

/// <summary>Start screen: login with an account of this PC, or create a new one.</summary>
public class LoginViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AccountService _accounts;

    public LoginViewModel(MainViewModel main, bool canCancel)
    {
        _main = main;
        _accounts = main.Accounts;
        CanCancel = canCancel;
        ExistingAccounts = _accounts.Accounts.OrderBy(a => a.Username, StringComparer.CurrentCultureIgnoreCase).ToList();
        _isCreateMode = ExistingAccounts.Count == 0;
        _avatarColor = Palette.Random();

        LoginCommand = new RelayCommand(Login);
        CreateCommand = new RelayCommand(Create);
        ToggleModeCommand = new RelayCommand(() => { IsCreateMode = !IsCreateMode; Error = null; });
        PickImageCommand = new RelayCommand(() => { if (_main.Dialogs.PickSquareImage() is { } image) AvatarImage = image; });
        RemoveImageCommand = new RelayCommand(() => AvatarImage = null);
        PickAccountCommand = new RelayCommand<Account>(a => { if (a != null) { Username = a.Username; IsCreateMode = false; Error = null; } });
        CancelCommand = new RelayCommand(() => _main.ShowProjects());
    }

    public IReadOnlyList<Account> ExistingAccounts { get; }
    public bool HasExistingAccounts => ExistingAccounts.Count > 0;
    public bool CanCancel { get; }
    public IReadOnlyList<string> Colors => Palette.Colors;

    private bool _isCreateMode;
    public bool IsCreateMode
    {
        get => _isCreateMode;
        set
        {
            if (!SetProperty(ref _isCreateMode, value)) return;
            OnPropertyChanged(nameof(IsLoginMode));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public bool IsLoginMode => !IsCreateMode;
    public string Heading => IsCreateMode ? "Create your account" : "Welcome back";
    public string ToggleText => IsCreateMode ? "I already have an account" : "Create a new account";

    private string _username = "";
    public string Username
    {
        get => _username;
        set { if (SetProperty(ref _username, value)) OnPropertyChanged(nameof(Initials)); }
    }

    public string Initials => Account.MakeInitials(Username);

    private string _password = "";
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    private string _confirmPassword = "";
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }

    private string _email = "";
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    private string _avatarColor;
    public string AvatarColor { get => _avatarColor; set => SetProperty(ref _avatarColor, value); }

    private string? _avatarImage;
    /// <summary>Optional profile picture (square PNG, base64) shown instead of the initials.</summary>
    public string? AvatarImage
    {
        get => _avatarImage;
        set { if (SetProperty(ref _avatarImage, value)) OnPropertyChanged(nameof(HasAvatarImage)); }
    }

    public bool HasAvatarImage => !string.IsNullOrEmpty(_avatarImage);
    public ICommand PickImageCommand { get; }
    public ICommand RemoveImageCommand { get; }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public ICommand LoginCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand PickAccountCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Called by the Enter key: runs the action of the current mode.</summary>
    public void Submit()
    {
        if (IsCreateMode) Create(); else Login();
    }

    private void Login()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            Error = "Enter username and password.";
            return;
        }
        if (_accounts.Login(Username, Password) is null)
        {
            Error = "Wrong username or password.";
            return;
        }
        _main.OnLoggedIn();
    }

    private void Create()
    {
        Error = null;
        if (Password != ConfirmPassword)
        {
            Error = "The two passwords do not match.";
            return;
        }
        if (!Palette.IsValidHex(AvatarColor))
        {
            Error = "The avatar color must be a hex value like #1F6F8B.";
            return;
        }
        try
        {
            var account = _accounts.Create(Username, Password, Email, AvatarColor, AvatarImage);
            _accounts.StartSession(account);
            _main.OnLoggedIn();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>Dialog to create a new account or edit an existing one.</summary>
public class AccountEditorViewModel : DialogViewModel
{
    private readonly AccountService _accounts;
    private readonly Account? _existing;

    public AccountEditorViewModel(AccountService accounts, Account? existing, IDialogService? dialogs = null)
    {
        _accounts = accounts;
        _existing = existing;
        _username = existing?.Username ?? "";
        _email = existing?.Email ?? "";
        _avatarColor = existing?.AvatarColor ?? Palette.Random();
        _avatarImage = existing?.AvatarImage;
        CanPickImage = dialogs != null;
        PickImageCommand = new RelayCommand(() => { if (dialogs?.PickSquareImage() is { } image) AvatarImage = image; });
        RemoveImageCommand = new RelayCommand(() => AvatarImage = null);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public bool IsNew => _existing is null;
    public string Title => IsNew ? "New account" : "Edit account";
    public string PasswordLabel => IsNew ? "Password" : "New password (leave empty to keep the current one)";
    public IReadOnlyList<string> Colors => Palette.Colors;

    /// <summary>The created / edited account, available after the dialog closes with OK.</summary>
    public Account? Result { get; private set; }

    private string _username;
    public string Username
    {
        get => _username;
        set { if (SetProperty(ref _username, value)) OnPropertyChanged(nameof(Initials)); }
    }

    public string Initials => Account.MakeInitials(Username);

    private string _email;
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    private string _avatarColor;
    public string AvatarColor { get => _avatarColor; set => SetProperty(ref _avatarColor, value); }

    private string? _avatarImage;
    /// <summary>Optional profile picture (square PNG, base64) shown instead of the initials.</summary>
    public string? AvatarImage
    {
        get => _avatarImage;
        set { if (SetProperty(ref _avatarImage, value)) OnPropertyChanged(nameof(HasAvatarImage)); }
    }

    public bool HasAvatarImage => !string.IsNullOrEmpty(_avatarImage);
    public bool CanPickImage { get; }
    public ICommand PickImageCommand { get; }
    public ICommand RemoveImageCommand { get; }

    private string _password = "";
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    private string _confirmPassword = "";
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void Save()
    {
        Error = null;
        if (Password != ConfirmPassword)
        {
            Error = "The two passwords do not match.";
            return;
        }
        if (!Palette.IsValidHex(AvatarColor))
        {
            Error = "The avatar color must be a hex value like #1F6F8B.";
            return;
        }
        try
        {
            if (_existing is null)
                Result = _accounts.Create(Username, Password, Email, AvatarColor, AvatarImage);
            else
            {
                _accounts.Update(_existing, Username, Email, AvatarColor, Password, AvatarImage);
                Result = _existing;
            }
            Close(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>Global account manager: every account stored on this PC.</summary>
public class AccountManagerViewModel : DialogViewModel
{
    private readonly AccountService _accounts;
    private readonly IDialogService _dialogs;

    public AccountManagerViewModel(AccountService accounts, IDialogService dialogs)
    {
        _accounts = accounts;
        _dialogs = dialogs;
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit);
        DeleteCommand = new RelayCommand(Delete);
        SwitchCommand = new RelayCommand(Switch);
        CloseCommand = new RelayCommand(() => Close(true));
        Reload(null);
    }

    public ObservableCollection<AccountRow> Items { get; } = new();

    private AccountRow? _selected;
    public AccountRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => _selected != null;

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SwitchCommand { get; }
    public ICommand CloseCommand { get; }

    private void Reload(string? selectId)
    {
        Items.Clear();
        foreach (var account in _accounts.Accounts.OrderBy(a => a.Username, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new AccountRow(account, account.Id == _accounts.Current?.Id));
        Selected = Items.FirstOrDefault(i => i.Account.Id == selectId) ?? Items.FirstOrDefault();
    }

    /// <summary>Operations on an account that is not the logged-in one require its password.</summary>
    private bool Authorize(Account account, string action)
    {
        if (account.Id == _accounts.Current?.Id) return true;
        var password = _dialogs.PromptPassword("Password required", $"Enter the password of '{account.Username}' to {action}.");
        if (password is null) return false;
        if (_accounts.VerifyPassword(account, password)) return true;
        _dialogs.Error("Wrong password.");
        return false;
    }

    private void Add()
    {
        var vm = new AccountEditorViewModel(_accounts, null, _dialogs);
        if (_dialogs.ShowDialog(vm) == true) Reload(vm.Result?.Id);
    }

    private void Edit()
    {
        if (Selected is null || !Authorize(Selected.Account, "edit it")) return;
        var vm = new AccountEditorViewModel(_accounts, Selected.Account, _dialogs);
        if (_dialogs.ShowDialog(vm) == true) Reload(vm.Result?.Id);
    }

    private void Delete()
    {
        if (Selected is null) return;
        var account = Selected.Account;
        if (!Authorize(account, "delete it")) return;
        if (!_dialogs.Confirm(
                $"Delete the account '{account.Username}' from this PC?\n\n" +
                "Projects keep their own copy of the account, so the history of cards and comments is preserved.",
                "Delete account")) return;
        _accounts.Delete(account);
        Reload(null);
    }

    private void Switch()
    {
        if (Selected is null || Selected.IsCurrent) return;
        var account = Selected.Account;
        var password = _dialogs.PromptPassword("Switch account", $"Enter the password of '{account.Username}'.");
        if (password is null) return;
        if (!_accounts.VerifyPassword(account, password))
        {
            _dialogs.Error("Wrong password.");
            return;
        }
        _accounts.StartSession(account);
        Reload(account.Id);
    }
}

/// <summary>Row of the account manager list.</summary>
public class AccountRow
{
    public AccountRow(Account account, bool isCurrent)
    {
        Account = account;
        IsCurrent = isCurrent;
    }

    public Account Account { get; }
    public bool IsCurrent { get; }
    public string Username => Account.Username;
    public string Initials => Account.Initials;
    public string AvatarColor => Account.AvatarColor;
    public string? AvatarImage => Account.AvatarImage;
    public string Details =>
        (string.IsNullOrWhiteSpace(Account.Email) ? "no email" : Account.Email) +
        $"  ·  created {Account.CreatedAt.ToLocalTime():yyyy-MM-dd}" +
        (IsCurrent ? "  ·  logged in" : "");
}
