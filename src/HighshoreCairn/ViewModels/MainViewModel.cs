using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

/// <summary>Entry of the File > Recent menu.</summary>
public class RecentItem
{
    public RecentItem(string name, string folder, ICommand openCommand)
    {
        Name = MenuText.Escape(name);
        Folder = folder;
        OpenCommand = openCommand;
    }

    public string Name { get; }
    public string Folder { get; }
    public ICommand OpenCommand { get; }
}

/// <summary>
/// Root ViewModel of the main window: holds the logged-in account and decides which
/// screen is shown (login, project list or board).
/// </summary>
public class MainViewModel : ObservableObject
{
    private readonly IThemeService? _theme;
    private readonly SettingsService _settings;

    public MainViewModel(AccountService accounts, ProjectService projects, IDialogService dialogs,
        IThemeService? theme = null, SettingsService? settings = null, ISoundService? sound = null)
    {
        Accounts = accounts;
        Projects = projects;
        Dialogs = dialogs;
        Sound = sound;
        _theme = theme;
        _settings = settings ?? new SettingsService();
        ToggleThemeCommand = new RelayCommand(() => IsDarkMode = !IsDarkMode);
        ShowKanbanCommand = new RelayCommand(() => { if (Board != null) Board.Mode = ProjectMode.Kanban; });
        ShowWhiteboardCommand = new RelayCommand(() => { if (Board != null) Board.Mode = ProjectMode.Whiteboard; });
        ShowDocsCommand = new RelayCommand(() => { if (Board != null) Board.Mode = ProjectMode.Docs; });
        Accounts.CurrentChanged += RaiseAccountChanged;

        NewProjectCommand = new RelayCommand(NewProject);
        OpenFolderCommand = new RelayCommand(OpenProjectFolder);
        CloseProjectCommand = new RelayCommand(() => { if (IsLoggedIn) ShowProjects(); });
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        ProjectSettingsCommand = new RelayCommand(() => Board?.OpenSettings());
        ManageTagsCommand = new RelayCommand(() => Board?.ManageTags());
        ArchiveCommand = new RelayCommand(() => Board?.ArchiveCommand.Execute(null));
        ManageAccountsCommand = new RelayCommand(ManageAccounts);
        EditProfileCommand = new RelayCommand(EditProfile);
        SwitchAccountCommand = new RelayCommand(() => ShowLogin(canCancel: IsLoggedIn));
        LogoutCommand = new RelayCommand(Logout);
        AboutCommand = new RelayCommand(About);
    }

    public AccountService Accounts { get; }
    public ProjectService Projects { get; }
    public IDialogService Dialogs { get; }
    /// <summary>Notification sounds (null in the tests).</summary>
    public ISoundService? Sound { get; }

    /// <summary>Markdown / rich text conversion (set by the application; null in the tests).</summary>
    public IDocConverter? DocConverter { get; set; }

    /// <summary>The built-in icon set (set by the application; empty in the tests).</summary>
    public IconLibrary Icons { get; set; } = new(null);

    /// <summary>Set by the main window: closes the application.</summary>
    public Action? ExitRequested { get; set; }

    // ------------------------------------------------------------------ current screen

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        private set
        {
            var previous = _currentView as BoardViewModel;
            if (!SetProperty(ref _currentView, value)) return;
            previous?.Close(); // stops the timer and saves the open documents
            OnPropertyChanged(nameof(Board));
            OnPropertyChanged(nameof(HasBoard));
            OnPropertyChanged(nameof(IsProjectList));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>True while the home screen (the list of projects) is shown.</summary>
    public bool IsProjectList => _currentView is ProjectListViewModel;

    public BoardViewModel? Board => _currentView as BoardViewModel;
    public bool HasBoard => Board != null;

    public string WindowTitle => Board is null
        ? "Cairn"
        : $"{Board.Project.Info.Name} – Cairn";

    // ------------------------------------------------------------------ account header

    public bool IsLoggedIn => Accounts.Current != null;
    public string AccountName => Accounts.Current?.Username ?? "Not logged in";
    public string AccountInitials => Accounts.Current?.Initials ?? "?";
    public string AccountColor => Accounts.Current?.AvatarColor ?? "#8D8D8D";
    public string? AccountImage => Accounts.Current?.AvatarImage;

    private void RaiseAccountChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountInitials));
        OnPropertyChanged(nameof(AccountColor));
        OnPropertyChanged(nameof(AccountImage));
    }

    // ------------------------------------------------------------------ commands

    public ICommand NewProjectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseProjectCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand ProjectSettingsCommand { get; }
    public ICommand ManageTagsCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand ManageAccountsCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SwitchAccountCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ShowKanbanCommand { get; }
    public ICommand ShowWhiteboardCommand { get; }
    public ICommand ShowDocsCommand { get; }

    // ------------------------------------------------------------------ theme

    /// <summary>Dark / light theme. The choice is remembered on this PC (settings.json).</summary>
    public bool IsDarkMode
    {
        get => _settings.Current.Theme == "Dark";
        set
        {
            if (IsDarkMode == value) return;
            _settings.Current.Theme = value ? "Dark" : "Light";
            _settings.Save();
            _theme?.Apply(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeToolTip));
            Board?.OnThemeChanged(); // the columns have separate colors for the two themes
        }
    }

    public string ThemeToolTip => IsDarkMode ? "Switch to the light theme" : "Switch to the dark theme";

    public ObservableCollection<RecentItem> RecentProjects { get; } = new();
    public bool HasRecent => RecentProjects.Count > 0;

    // ------------------------------------------------------------------ navigation

    /// <summary>Application start: auto-login with the session stored on this PC, otherwise ask to login.</summary>
    public void Start()
    {
        _settings.Load();
        _theme?.Apply(IsDarkMode);
        OnPropertyChanged(nameof(IsDarkMode));
        OnPropertyChanged(nameof(ThemeToolTip));
        Accounts.Load();
        if (Accounts.TryAutoLogin() != null) ShowProjects();
        else ShowLogin(canCancel: false);
    }

    public void ShowLogin(bool canCancel) => CurrentView = new LoginViewModel(this, canCancel);

    public void OnLoggedIn() => ShowProjects();

    public void ShowProjects()
    {
        if (!IsLoggedIn)
        {
            ShowLogin(canCancel: false);
            return;
        }
        CurrentView = new ProjectListViewModel(this);
        RefreshRecent();
    }

    public void RefreshRecent()
    {
        RecentProjects.Clear();
        try
        {
            foreach (var entry in Projects.List().Where(e => e.LastOpenedAt != null).Take(8))
            {
                var captured = entry;
                RecentProjects.Add(new RecentItem(entry.Info.Name, entry.Folder,
                    new RelayCommand(() => OpenProject(captured))));
            }
        }
        catch { /* the recent list is optional */ }
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>Called when the application closes.</summary>
    public void Shutdown() => Board?.Close();

    /// <summary>Called when the open project's metadata changed (settings dialog).</summary>
    public void OnProjectChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        RefreshRecent();
    }

    // ------------------------------------------------------------------ projects

    public void NewProject()
    {
        if (Accounts.Current is null)
        {
            ShowLogin(canCancel: false);
            return;
        }
        var vm = new ProjectEditorViewModel(Projects, Dialogs, Accounts.Current);
        if (Dialogs.ShowDialog(vm) == true && vm.Result != null) OpenProject(vm.Result);
    }

    /// <summary>File > Open: adds an existing project folder (e.g. a git clone) and opens it.</summary>
    public void OpenProjectFolder()
    {
        if (Accounts.Current is null)
        {
            ShowLogin(canCancel: false);
            return;
        }
        var folder = Dialogs.PickFolder();
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            OpenProject(Projects.AddExisting(folder));
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
    }

    public void OpenProject(ProjectEntry entry)
    {
        var fresh = Projects.TryLoadEntry(entry.Folder);
        if (fresh is null)
        {
            Dialogs.Error($"The project folder is missing or damaged:\n{entry.Folder}");
            return;
        }
        if (!EnsureProjectAccess(fresh)) return;
        try
        {
            Projects.MarkOpened(fresh.Folder);
            CurrentView = new BoardViewModel(this, fresh);
        }
        catch (Exception ex)
        {
            Dialogs.Error("The project could not be opened.\n\n" + ex.Message);
        }
        RefreshRecent();
    }

    /// <summary>
    /// Verifies that the logged-in account is authorized in the project. When it is not,
    /// the user can login as a project member, join, or create a new account.
    /// </summary>
    public bool EnsureProjectAccess(ProjectEntry entry)
    {
        var current = Accounts.Current;
        if (current is null)
        {
            ShowLogin(canCancel: false);
            return false;
        }

        AccountStore store;
        try
        {
            store = Projects.LoadAccounts(entry.Folder);
        }
        catch (Exception ex)
        {
            Dialogs.Error("The project accounts could not be read.\n\n" + ex.Message);
            return false;
        }

        var member = store.Accounts.FirstOrDefault(a => a.Id == current.Id);
        if (member != null)
        {
            SyncProfile(entry.Folder, store, member, current);
            return true;
        }

        if (store.Accounts.Count == 0)
        {
            // Project without accounts (e.g. accounts.json missing): the first user becomes a member.
            store.Accounts.Add(current.CloneForProject());
            Projects.SaveAccounts(entry.Folder, store);
            return true;
        }

        return Dialogs.ShowDialog(new ProjectAccessViewModel(entry, store, Projects, Accounts, Dialogs)) == true;
    }

    /// <summary>Keeps the project copy of the account aligned with the global one (name, color, password).</summary>
    private void SyncProfile(string folder, AccountStore store, Account member, Account global)
    {
        if (member.Username == global.Username && member.Email == global.Email &&
            member.AvatarColor == global.AvatarColor && member.AvatarImage == global.AvatarImage &&
            member.PasswordHash == global.PasswordHash) return;
        var index = store.Accounts.IndexOf(member);
        store.Accounts[index] = global.CloneForProject();
        try { Projects.SaveAccounts(folder, store); }
        catch { /* not critical */ }
    }

    // ------------------------------------------------------------------ accounts

    private void ManageAccounts()
    {
        var before = Accounts.Current?.Id;
        Dialogs.ShowDialog(new AccountManagerViewModel(Accounts, Dialogs));
        AfterAccountsChanged(before);
    }

    private void EditProfile()
    {
        if (Accounts.Current is null) return;
        var before = Accounts.Current.Id;
        Dialogs.ShowDialog(new AccountEditorViewModel(Accounts, Accounts.Current, Dialogs));
        AfterAccountsChanged(before);
    }

    private void AfterAccountsChanged(string? previousAccountId)
    {
        RaiseAccountChanged();
        if (Accounts.Current is null)
        {
            ShowLogin(canCancel: false);
        }
        else if (Accounts.Current.Id != previousAccountId)
        {
            ShowProjects(); // a different user: every project must be re-checked for access
        }
        else if (Board != null)
        {
            // Same user, maybe with a new name/color: refresh the project copy and the board.
            EnsureProjectAccess(Board.Project);
            Board.ReloadMembers();
        }
    }

    private void Logout()
    {
        // Leave the project first: its documents are saved while the user is still logged in.
        CurrentView = null;
        Accounts.Logout();
        ShowLogin(canCancel: false);
    }

    private void About()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Dialogs.Info(
            $"Cairn {version?.ToString(3)}\n\n" +
            "Personal, offline Kanban board, roadmap, whiteboard and documentation with local accounts.\n" +
            "No cloud: every project is a folder of plain files, ready for git.\n\n" +
            $"Application data: {AppPaths.Root}\n\n" +
            "Milestone icons: Ionicons (MIT license, ionic.io/ionicons).\n\n\n" +
            "highshore.studio@gmail.com", "About Cairn");
    }
}
