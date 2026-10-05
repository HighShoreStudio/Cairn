using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

/// <summary>A project tile in the project list.</summary>
public class ProjectItem
{
    public ProjectItem(ProjectEntry entry, bool isTemplate = false)
    {
        Entry = entry;
        IsTemplate = isTemplate;
    }

    public ProjectEntry Entry { get; }
    public string Name => Entry.Info.Name;
    public string Description => string.IsNullOrWhiteSpace(Entry.Info.Description) ? "No description" : Entry.Info.Description;
    public string Color => Entry.Info.Color;
    public string Initials => Account.MakeInitials(Entry.Info.Name);
    /// <summary>Optional square picture (base64 PNG) shown instead of the colored initials.</summary>
    public string? Icon => Entry.Info.Icon;
    public bool HasIcon => !string.IsNullOrEmpty(Entry.Info.Icon);
    /// <summary>This project is the source of the default template used by new projects.</summary>
    public bool IsTemplate { get; }
    public string Folder => Entry.Folder;
    public string Meta => $"{Entry.ColumnCount} columns  ·  {Entry.CardCount} cards  ·  {Entry.MemberCount} members";
    public string Dates =>
        $"Owner: {Entry.Info.OwnerName}  ·  Created {Entry.Info.CreatedAt.ToLocalTime():yyyy-MM-dd}" +
        (Entry.LastOpenedAt is null ? "" : $"  ·  Last opened {Entry.LastOpenedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}");
}

/// <summary>Home screen: the list of projects known on this PC.</summary>
public class ProjectListViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ProjectListViewModel(MainViewModel main)
    {
        _main = main;
        NewCommand = new RelayCommand(() => _main.NewProject());
        AddExistingCommand = new RelayCommand(() => _main.OpenProjectFolder());
        OpenCommand = new RelayCommand<ProjectItem>(item => Open(item ?? Selected));
        DeleteCommand = new RelayCommand(Delete);
        SettingsCommand = new RelayCommand(Settings);
        ForgetCommand = new RelayCommand(Forget);
        RestoreTemplateCommand = new RelayCommand(RestoreTemplate);
        OpenTrashCommand = new RelayCommand(() =>
        {
            if (Selected != null && HasTrash) _main.Dialogs.OpenFolder(DocsService.TrashPath(Selected.Folder));
            OnPropertyChanged(nameof(HasTrash));
        });
        Reload();
    }

    public ObservableCollection<ProjectItem> Projects { get; } = new();

    private ProjectItem? _selected;
    public ProjectItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasTrash));
        }
    }

    public bool HasSelection => _selected != null;

    /// <summary>
    /// The selected project has deleted documents in docs/.trash: the "Trash Folder" button appears
    /// and opens that folder in Explorer (the app never empties it by itself).
    /// </summary>
    public bool HasTrash => _selected != null && DocsService.HasTrash(_selected.Folder);
    public ICommand OpenTrashCommand { get; }

    /// <summary>The trash may have been emptied (or filled) outside the app: called when the window comes back.</summary>
    public void RefreshTrash() => OnPropertyChanged(nameof(HasTrash));

    public bool IsEmpty => Projects.Count == 0;
    public bool HasProjects => Projects.Count > 0;

    // ------------------------------------------------------------------ default template

    private ProjectTemplate? _template;

    /// <summary>A project was chosen as "default template": new projects start from its customization.</summary>
    public bool HasTemplate => _template != null;

    public string TemplateToolTip => _template is null ? "" :
        $"New projects currently start from the customization of '{_template.SourceProjectName}' " +
        $"(saved {_template.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}).\n" +
        "Click to go back to the original template: TODO, In Progress, Done and the default settings.";

    public ICommand NewCommand { get; }
    public ICommand AddExistingCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand RestoreTemplateCommand { get; }

    public void Reload(string? selectFolder = null)
    {
        selectFolder ??= Selected?.Folder;
        _template = _main.Projects.LoadTemplate();
        Projects.Clear();
        foreach (var entry in _main.Projects.List())
            Projects.Add(new ProjectItem(entry, _template != null && _template.SourceProjectId == entry.Info.Id));
        Selected = Projects.FirstOrDefault(p => string.Equals(p.Folder, selectFolder, StringComparison.OrdinalIgnoreCase))
                   ?? Projects.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateToolTip));
        OnPropertyChanged(nameof(HasTrash));
    }

    private void Open(ProjectItem? item)
    {
        if (item != null) _main.OpenProject(item.Entry);
    }

    private void Delete()
    {
        if (Selected is null) return;
        var hasDocs = Directory.Exists(Path.Combine(Selected.Folder, DocsService.FolderName));
        if (!_main.Dialogs.Confirm(
                $"Delete the project '{Selected.Name}'?\n\n" +
                $"Its files in\n{Selected.Folder}\nwill be permanently deleted (board, whiteboards, accounts and backups)." +
                (hasDocs ? "\n\nThe documents in its 'docs' folder are NOT deleted." : ""),
                "Delete project")) return;
        try
        {
            _main.Projects.Delete(Selected.Folder);
        }
        catch (Exception ex)
        {
            _main.Dialogs.Error("The project could not be deleted completely.\n\n" + ex.Message);
        }
        Reload();
        _main.RefreshRecent();
    }

    private void Forget()
    {
        if (Selected is null) return;
        if (!_main.Dialogs.Confirm(
                $"Remove '{Selected.Name}' from the list?\n\nThe project files are NOT deleted. " +
                "Note: projects inside the default projects folder always appear in the list.",
                "Remove from list")) return;
        _main.Projects.Forget(Selected.Folder);
        Reload();
        _main.RefreshRecent();
    }

    private void Settings()
    {
        if (Selected is null) return;
        if (!_main.EnsureProjectAccess(Selected.Entry)) return;
        var vm = new ProjectSettingsViewModel(Selected.Entry, _main.Projects, _main.Accounts, _main.Dialogs, _main.Sound, _main.Icons);
        _main.Dialogs.ShowDialog(vm);
        Reload();
        _main.RefreshRecent();
    }

    private void RestoreTemplate()
    {
        if (_template is null) return;
        if (!_main.Dialogs.Confirm(
                $"Go back to the original template for new projects?\n\n" +
                $"New projects will no longer start from the customization of '{_template.SourceProjectName}'. " +
                "Existing projects are not changed.",
                "Restore Original Template")) return;
        try
        {
            _main.Projects.ClearTemplate();
        }
        catch (Exception ex)
        {
            _main.Dialogs.Error("The template could not be removed.\n\n" + ex.Message);
        }
        Reload();
    }
}

/// <summary>"New Project" dialog.</summary>
public class ProjectEditorViewModel : DialogViewModel
{
    private readonly ProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly Account _owner;
    private bool _folderCustomized;

    public ProjectEditorViewModel(ProjectService projects, IDialogService dialogs, Account owner)
    {
        _projects = projects;
        _dialogs = dialogs;
        _owner = owner;
        var template = projects.LoadTemplate();
        _color = template != null && Palette.IsValidHex(template.Color) ? template.Color : Palette.Colors[0];
        TemplateNote = template is null ? "" :
            $"Columns, colors, tags and settings start from the default template ('{template.SourceProjectName}').";
        _folder = ProjectService.DefaultFolderFor("project");
        BrowseCommand = new RelayCommand(Browse);
        ResetFolderCommand = new RelayCommand(() => { _folderCustomized = false; Folder = ProjectService.DefaultFolderFor(Name); _folderCustomized = false; });
        PickIconCommand = new RelayCommand(() => { if (_dialogs.PickSquareImage() is { } image) Icon = image; });
        RemoveIconCommand = new RelayCommand(() => Icon = null);
        CreateCommand = new RelayCommand(Create);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public IReadOnlyList<string> Colors => Palette.Colors;
    public ProjectEntry? Result { get; private set; }

    /// <summary>Explains that a default template is in use (empty when the built-in defaults apply).</summary>
    public string TemplateNote { get; }
    public bool HasTemplateNote => TemplateNote.Length > 0;

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(Initials));
            if (!_folderCustomized)
            {
                _folder = ProjectService.DefaultFolderFor(value);
                OnPropertyChanged(nameof(Folder));
            }
        }
    }

    public string Initials => Account.MakeInitials(Name);

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private string _color;
    public string Color { get => _color; set => SetProperty(ref _color, value); }

    private string? _icon;
    /// <summary>Optional square picture (base64 PNG) shown instead of the colored initials.</summary>
    public string? Icon
    {
        get => _icon;
        set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
    }

    public bool HasIcon => !string.IsNullOrEmpty(_icon);

    private string _folder;
    public string Folder
    {
        get => _folder;
        set { if (SetProperty(ref _folder, value)) _folderCustomized = true; }
    }

    public ICommand BrowseCommand { get; }
    public ICommand ResetFolderCommand { get; }
    public ICommand PickIconCommand { get; }
    public ICommand RemoveIconCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    private void Browse()
    {
        var picked = _dialogs.PickFolder();
        if (string.IsNullOrEmpty(picked)) return;
        // A non-empty folder was picked: create the project in a sub-folder named after the project.
        var target = picked;
        try
        {
            if (Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any())
                target = Path.Combine(picked, ProjectService.Slug(Name));
        }
        catch { /* keep the picked folder */ }
        Folder = target;
    }

    private void Create()
    {
        Error = null;
        if (!Palette.IsValidHex(Color))
        {
            Error = "The color must be a hex value like #1F6F8B.";
            return;
        }
        try
        {
            Result = _projects.Create(Name, Description, Folder, Color, _owner, Icon);
            Close(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>Row of the member list in the project settings.</summary>
public class MemberRow
{
    public MemberRow(Account account, bool isOwner, bool isCurrent)
    {
        Account = account;
        IsOwner = isOwner;
        IsCurrent = isCurrent;
    }

    public Account Account { get; }
    public bool IsOwner { get; }
    public bool IsCurrent { get; }
    public string Username => Account.Username;
    public string Initials => Account.Initials;
    public string AvatarColor => Account.AvatarColor;
    public string? AvatarImage => Account.AvatarImage;
    public string Role => (IsOwner ? "Owner" : "Member") + (IsCurrent ? " (you)" : "");
    public bool CanRemove => !IsCurrent;
}

/// <summary>A choice of a settings combo box: a value with the text shown to the user.</summary>
public class SettingOption<T>
{
    public SettingOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }
    public string Label { get; }
}

/// <summary>Project settings: general data, authorized users, backups, tomato timer, documentation.</summary>
public class ProjectSettingsViewModel : DialogViewModel
{
    private readonly ProjectEntry _entry;
    private readonly ProjectService _projects;
    private readonly AccountService _accounts;
    private readonly IDialogService _dialogs;
    private readonly ISoundService? _sound;
    private readonly AccountStore _store;

    private readonly IconLibrary _icons;
    private readonly RoadmapService _roadmapFiles;

    public static readonly IReadOnlyList<string> TabNames = new[] { "General", "Users", "Backups", "Timer", "Documentation", "Roadmap" };

    public ProjectSettingsViewModel(ProjectEntry entry, ProjectService projects, AccountService accounts,
        IDialogService dialogs, ISoundService? sound = null, IconLibrary? icons = null, string? tab = null)
    {
        _entry = entry;
        _projects = projects;
        _accounts = accounts;
        _dialogs = dialogs;
        _sound = sound;
        _icons = icons ?? new IconLibrary(null);
        _roadmapFiles = new RoadmapService(entry.Folder);
        _selectedTab = Math.Max(0, TabNames.ToList().IndexOf(tab ?? "General"));
        _store = projects.LoadAccounts(entry.Folder);

        _name = entry.Info.Name;
        _description = entry.Info.Description;
        _color = entry.Info.Color;
        _icon = entry.Info.Icon;

        var timer = entry.Info.Settings.Pomodoro;
        _focusMinutes = timer.FocusMinutes;
        _shortBreakMinutes = timer.ShortBreakMinutes;
        _longBreakMinutes = timer.LongBreakMinutes;
        _longBreakEvery = timer.LongBreakEvery;
        _autoStartBreaks = timer.AutoStartBreaks;
        _autoStartFocus = timer.AutoStartFocus;
        _timerSound = Sounds.Contains(timer.Sound) ? timer.Sound : "Chime";
        _customSoundFile = timer.CustomSoundFile ?? "";
        _volume = Math.Clamp(timer.Volume, 0, 100);
        _timerHidden = timer.Hidden;

        var docs = entry.Info.Settings.Docs;
        _importMode = docs.ImportMode;
        _openInPreview = docs.OpenInPreview;

        var roadmap = entry.Info.Settings.Roadmap;
        _showMilestones = roadmap.ShowMilestones;
        _milestonesName = roadmap.MilestonesName;
        _milestonesColor = roadmap.MilestonesColor;
        _showCustom = roadmap.ShowCustom;
        _customName = roadmap.CustomName;
        _customColor = roadmap.CustomColor;
        _showWeekNumber = roadmap.ShowWeekNumber;
        _showInProgress = roadmap.ShowInProgress;
        _autoEndDate = roadmap.AutoEndDate;
        _selectionBackLight = roadmap.SelectionBackLight;
        _selectionTextLight = roadmap.SelectionTextLight;
        _selectionBackDark = roadmap.SelectionBackDark;
        _selectionTextDark = roadmap.SelectionTextDark;
        foreach (var type in roadmap.MilestoneTypes) MilestoneTypes.Add(new MilestoneTypeRow(type, _roadmapFiles.IconsFolder));
        AddMilestoneTypeCommand = new RelayCommand(() =>
            MilestoneTypes.Add(new MilestoneTypeRow(new MilestoneType { Name = "New type", Color = Palette.Random() }, _roadmapFiles.IconsFolder)));
        RemoveMilestoneTypeCommand = new RelayCommand<MilestoneTypeRow>(row => { if (row != null) MilestoneTypes.Remove(row); });
        PickMilestoneIconCommand = new RelayCommand<MilestoneTypeRow>(PickMilestoneIcon);
        ResetSelectionColorsCommand = new RelayCommand(() =>
        {
            var defaults = new RoadmapSettings();
            SelectionBackLight = defaults.SelectionBackLight;
            SelectionTextLight = defaults.SelectionTextLight;
            SelectionBackDark = defaults.SelectionBackDark;
            SelectionTextDark = defaults.SelectionTextDark;
        });

        AddMemberCommand = new RelayCommand(AddMember);
        RemoveMemberCommand = new RelayCommand<MemberRow>(RemoveMember);
        BackupNowCommand = new RelayCommand(BackupNow);
        RestoreCommand = new RelayCommand(Restore);
        OpenFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_entry.Folder));
        PickIconCommand = new RelayCommand(() => { if (_dialogs.PickSquareImage() is { } image) Icon = image; });
        RemoveIconCommand = new RelayCommand(() => Icon = null);
        SetTemplateCommand = new RelayCommand(SetTemplate);
        BrowseSoundCommand = new RelayCommand(BrowseSound);
        TestSoundCommand = new RelayCommand(() => _sound?.Play(TimerSound, Volume, CustomSoundFile));
        ResetTimerCommand = new RelayCommand(ResetTimer);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));

        RefreshMembers();
        RefreshBackups();
        RefreshTemplate();
    }

    public IReadOnlyList<string> Colors => Palette.Colors;
    public string Folder => _entry.Folder;

    private int _selectedTab;
    /// <summary>Index of the tab on screen (see <see cref="TabNames"/>).</summary>
    public int SelectedTab { get => _selectedTab; set => SetProperty(ref _selectedTab, value); }

    /// <summary>True when name/description/members were saved.</summary>
    public bool Saved { get; private set; }

    /// <summary>True when a backup was restored: an open board must be reloaded from disk.</summary>
    public bool Restored { get; private set; }

    /// <summary>Called right before a manual backup/restore, so an open board can flush its state to disk.</summary>
    public Action? BeforeBackup { get; set; }

    // ------------------------------------------------------------------ general

    private string _name;
    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Initials)); }
    }

    public string Initials => Account.MakeInitials(Name);

    private string _description;
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private string _color;
    public string Color { get => _color; set => SetProperty(ref _color, value); }

    private string? _icon;
    /// <summary>Optional square picture (base64 PNG) shown instead of the colored initials.</summary>
    public string? Icon
    {
        get => _icon;
        set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
    }

    public bool HasIcon => !string.IsNullOrEmpty(_icon);

    public ICommand PickIconCommand { get; }
    public ICommand RemoveIconCommand { get; }

    // ------------------------------------------------------------------ default template

    private string _templateStatus = "";
    /// <summary>Tells whether this project is the current default template.</summary>
    public string TemplateStatus { get => _templateStatus; private set => SetProperty(ref _templateStatus, value); }

    private bool _isTemplate;
    public bool IsTemplate { get => _isTemplate; private set => SetProperty(ref _isTemplate, value); }

    public ICommand SetTemplateCommand { get; }

    private void RefreshTemplate()
    {
        var template = _projects.LoadTemplate();
        IsTemplate = template != null && template.SourceProjectId == _entry.Info.Id;
        TemplateStatus = template is null
            ? "New projects use the original template (TODO, In Progress, Done)."
            : IsTemplate
                ? $"This project is the default template (saved {template.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                  "Click again to update it after changing the project."
                : $"The current default template comes from '{template.SourceProjectName}'.";
    }

    /// <summary>
    /// Copies the customization of this project (columns with names, order and colors, tags,
    /// saved filters, timer and documentation settings, project color) into the default template.
    /// </summary>
    private void SetTemplate()
    {
        Error = null;
        if (!Validate()) return;
        try
        {
            BeforeBackup?.Invoke(); // an open board writes its current state first
            _projects.SaveTemplate(_entry, Color, BuildSettings());
            RefreshTemplate();
            _dialogs.Info(
                "This project is now the default template.\n\n" +
                "New projects will start with its columns (names, order, colors), tags, saved filters, " +
                "timer, documentation and roadmap settings (rows, colors, milestone types and their icons).\n" +
                "Cards, whiteboards and documents are never copied.\n\n" +
                "Use 'Restore Original Template' in the project list to go back to the original defaults.",
                "Default Template");
        }
        catch (Exception ex)
        {
            _dialogs.Error("The template could not be saved.\n\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ users

    public ObservableCollection<MemberRow> Members { get; } = new();
    public ObservableCollection<Account> AvailableAccounts { get; } = new();

    private Account? _selectedAvailable;
    public Account? SelectedAvailable { get => _selectedAvailable; set => SetProperty(ref _selectedAvailable, value); }

    public bool HasAvailableAccounts => AvailableAccounts.Count > 0;

    // ------------------------------------------------------------------ backups

    public ObservableCollection<BackupInfo> Backups { get; } = new();

    private BackupInfo? _selectedBackup;
    public BackupInfo? SelectedBackup
    {
        get => _selectedBackup;
        set { if (SetProperty(ref _selectedBackup, value)) OnPropertyChanged(nameof(HasSelectedBackup)); }
    }

    public bool HasSelectedBackup => _selectedBackup != null;

    // ------------------------------------------------------------------ tomato timer

    private int _focusMinutes;
    public int FocusMinutes { get => _focusMinutes; set => SetProperty(ref _focusMinutes, value); }

    private int _shortBreakMinutes;
    public int ShortBreakMinutes { get => _shortBreakMinutes; set => SetProperty(ref _shortBreakMinutes, value); }

    private int _longBreakMinutes;
    public int LongBreakMinutes { get => _longBreakMinutes; set => SetProperty(ref _longBreakMinutes, value); }

    private int _longBreakEvery;
    public int LongBreakEvery { get => _longBreakEvery; set => SetProperty(ref _longBreakEvery, value); }

    private bool _autoStartBreaks;
    public bool AutoStartBreaks { get => _autoStartBreaks; set => SetProperty(ref _autoStartBreaks, value); }

    private bool _autoStartFocus;
    public bool AutoStartFocus { get => _autoStartFocus; set => SetProperty(ref _autoStartFocus, value); }

    public IReadOnlyList<string> Sounds { get; } = new[] { "None", "Chime", "Bell", "Digital", "Custom" };

    private string _timerSound;
    public string TimerSound
    {
        get => _timerSound;
        set { if (SetProperty(ref _timerSound, value)) OnPropertyChanged(nameof(IsCustomSound)); }
    }

    public bool IsCustomSound => _timerSound == "Custom";

    private string _customSoundFile;
    public string CustomSoundFile { get => _customSoundFile; set => SetProperty(ref _customSoundFile, value); }

    private int _volume;
    public int Volume
    {
        get => _volume;
        set { if (SetProperty(ref _volume, Math.Clamp(value, 0, 100))) OnPropertyChanged(nameof(VolumeText)); }
    }

    public string VolumeText => $"{_volume}%";

    private bool _timerHidden;
    /// <summary>Hides the timer from the project header.</summary>
    public bool TimerHidden { get => _timerHidden; set => SetProperty(ref _timerHidden, value); }

    public ICommand BrowseSoundCommand { get; }
    public ICommand TestSoundCommand { get; }
    public ICommand ResetTimerCommand { get; }

    private void BrowseSound()
    {
        var file = _dialogs.PickFile("Wave sound (*.wav)|*.wav");
        if (string.IsNullOrEmpty(file)) return;
        CustomSoundFile = file;
        TimerSound = "Custom";
    }

    private void ResetTimer()
    {
        var defaults = new PomodoroSettings();
        FocusMinutes = defaults.FocusMinutes;
        ShortBreakMinutes = defaults.ShortBreakMinutes;
        LongBreakMinutes = defaults.LongBreakMinutes;
        LongBreakEvery = defaults.LongBreakEvery;
        AutoStartBreaks = defaults.AutoStartBreaks;
        AutoStartFocus = defaults.AutoStartFocus;
    }

    // ------------------------------------------------------------------ documentation

    public IReadOnlyList<SettingOption<DocImportMode>> ImportModes { get; } = new[]
    {
        new SettingOption<DocImportMode>(DocImportMode.Copy, "Copy them into the project (docs folder)"),
        new SettingOption<DocImportMode>(DocImportMode.Link, "Link the original files (no copy)"),
        new SettingOption<DocImportMode>(DocImportMode.Ask, "Ask every time")
    };

    private DocImportMode _importMode;
    public DocImportMode ImportMode { get => _importMode; set => SetProperty(ref _importMode, value); }

    private bool _openInPreview;
    /// <summary>Documents open in the formatted view (true) or in the markdown view (false).</summary>
    public bool OpenInPreview
    {
        get => _openInPreview;
        set { if (SetProperty(ref _openInPreview, value)) OnPropertyChanged(nameof(OpenInMarkdown)); }
    }

    public bool OpenInMarkdown { get => !_openInPreview; set => OpenInPreview = !value; }

    // ------------------------------------------------------------------ roadmap

    private bool _showMilestones;
    public bool ShowMilestones { get => _showMilestones; set => SetProperty(ref _showMilestones, value); }

    private string _milestonesName;
    public string MilestonesName { get => _milestonesName; set => SetProperty(ref _milestonesName, value); }

    private string _milestonesColor;
    public string MilestonesColor { get => _milestonesColor; set => SetProperty(ref _milestonesColor, value); }

    private bool _showCustom;
    public bool ShowCustom { get => _showCustom; set => SetProperty(ref _showCustom, value); }

    private string _customName;
    public string CustomName { get => _customName; set => SetProperty(ref _customName, value); }

    private string _customColor;
    public string CustomColor { get => _customColor; set => SetProperty(ref _customColor, value); }

    private bool _showWeekNumber;
    public bool ShowWeekNumber { get => _showWeekNumber; set => SetProperty(ref _showWeekNumber, value); }

    private bool _showInProgress;
    public bool ShowInProgress { get => _showInProgress; set => SetProperty(ref _showInProgress, value); }

    private bool _autoEndDate;
    public bool AutoEndDate { get => _autoEndDate; set => SetProperty(ref _autoEndDate, value); }

    private string _selectionBackLight;
    public string SelectionBackLight { get => _selectionBackLight; set => SetProperty(ref _selectionBackLight, value); }

    private string _selectionTextLight;
    public string SelectionTextLight { get => _selectionTextLight; set => SetProperty(ref _selectionTextLight, value); }

    private string _selectionBackDark;
    public string SelectionBackDark { get => _selectionBackDark; set => SetProperty(ref _selectionBackDark, value); }

    private string _selectionTextDark;
    public string SelectionTextDark { get => _selectionTextDark; set => SetProperty(ref _selectionTextDark, value); }

    public ObservableCollection<MilestoneTypeRow> MilestoneTypes { get; } = new();

    public ICommand AddMilestoneTypeCommand { get; }
    public ICommand RemoveMilestoneTypeCommand { get; }
    public ICommand PickMilestoneIconCommand { get; }
    public ICommand ResetSelectionColorsCommand { get; }

    private void PickMilestoneIcon(MilestoneTypeRow? row)
    {
        if (row is null) return;
        var picker = new IconPickerViewModel(_icons, _dialogs, row.Icon, _roadmapFiles.IconsFolder, _roadmapFiles.ImportIcon);
        if (_dialogs.ShowDialog(picker) == true && !string.IsNullOrEmpty(picker.Result)) row.Icon = picker.Result;
    }

    // ------------------------------------------------------------------ commands

    public ICommand AddMemberCommand { get; }
    public ICommand RemoveMemberCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void RefreshMembers()
    {
        Members.Clear();
        foreach (var account in _store.Accounts)
            Members.Add(new MemberRow(account, account.Id == _entry.Info.OwnerId, account.Id == _accounts.Current?.Id));

        AvailableAccounts.Clear();
        foreach (var account in _accounts.Accounts.Where(a => _store.Accounts.All(m => m.Id != a.Id)))
            AvailableAccounts.Add(account);
        SelectedAvailable = AvailableAccounts.FirstOrDefault();
        OnPropertyChanged(nameof(HasAvailableAccounts));
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var backup in _projects.ListBackups(_entry.Folder)) Backups.Add(backup);
        SelectedBackup = Backups.FirstOrDefault();
    }

    private void AddMember()
    {
        if (SelectedAvailable is null) return;
        _store.Accounts.Add(SelectedAvailable.CloneForProject());
        RefreshMembers();
    }

    private void RemoveMember(MemberRow? row)
    {
        if (row is null || !row.CanRemove) return;
        if (!_dialogs.Confirm(
                $"Remove '{row.Username}' from the authorized users of this project?\n\n" +
                "Existing cards and comments keep the author's name.", "Remove user")) return;
        _store.Accounts.RemoveAll(a => a.Id == row.Account.Id);
        RefreshMembers();
    }

    private void BackupNow()
    {
        try
        {
            BeforeBackup?.Invoke();
            _projects.Backup(_entry.Folder);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            _dialogs.Error("Backup failed.\n\n" + ex.Message);
        }
    }

    private void Restore()
    {
        if (SelectedBackup is null) return;
        if (!_dialogs.Confirm(
                $"Restore the board from the backup of {SelectedBackup.CreatedAt:yyyy-MM-dd HH:mm:ss}?\n\n" +
                "The current board is backed up first, so this can be undone.", "Restore backup")) return;
        try
        {
            BeforeBackup?.Invoke();
            _projects.RestoreBackup(_entry.Folder, SelectedBackup);
            Restored = true;
            RefreshBackups();
            _dialogs.Info("Backup restored.");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Restore failed.\n\n" + ex.Message);
        }
    }

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "The project name is required.";
            return false;
        }
        if (!Palette.IsValidHex(Color))
        {
            Error = "The color must be a hex value like #1F6F8B.";
            return false;
        }
        if (FocusMinutes is < 1 or > 240 || ShortBreakMinutes is < 1 or > 240 || LongBreakMinutes is < 1 or > 240)
        {
            Error = "Timer durations must be between 1 and 240 minutes.";
            return false;
        }
        if (LongBreakEvery is < 1 or > 12)
        {
            Error = "The long break must come every 1-12 focus sessions.";
            return false;
        }
        if (IsCustomSound && !File.Exists(CustomSoundFile))
        {
            Error = "Choose a .wav file for the custom timer sound.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(MilestonesName) || string.IsNullOrWhiteSpace(CustomName))
        {
            Error = "Roadmap: the two special rows need a name.";
            return false;
        }
        var roadmapColors = new[] { MilestonesColor, CustomColor, SelectionBackLight, SelectionTextLight, SelectionBackDark, SelectionTextDark }
            .Concat(MilestoneTypes.Select(t => t.Color));
        if (roadmapColors.Any(c => !Palette.IsValidHex(c)))
        {
            Error = "Roadmap: colors must be hex values like #1F6F8B.";
            return false;
        }
        if (MilestoneTypes.Any(t => string.IsNullOrWhiteSpace(t.Name)))
        {
            Error = "Roadmap: every milestone type needs a name.";
            return false;
        }
        return true;
    }

    /// <summary>The settings as currently shown in the dialog.</summary>
    private ProjectSettings BuildSettings() => new()
    {
        Pomodoro = new PomodoroSettings
        {
            FocusMinutes = FocusMinutes,
            ShortBreakMinutes = ShortBreakMinutes,
            LongBreakMinutes = LongBreakMinutes,
            LongBreakEvery = LongBreakEvery,
            AutoStartBreaks = AutoStartBreaks,
            AutoStartFocus = AutoStartFocus,
            Sound = TimerSound,
            CustomSoundFile = string.IsNullOrWhiteSpace(CustomSoundFile) ? null : CustomSoundFile.Trim(),
            Volume = Volume,
            Hidden = TimerHidden
        },
        Docs = new DocsSettings { ImportMode = ImportMode, OpenInPreview = OpenInPreview },
        Roadmap = new RoadmapSettings
        {
            ShowMilestones = ShowMilestones,
            MilestonesName = (MilestonesName ?? "").Trim(),
            MilestonesColor = MilestonesColor,
            ShowCustom = ShowCustom,
            CustomName = (CustomName ?? "").Trim(),
            CustomColor = CustomColor,
            ShowWeekNumber = ShowWeekNumber,
            ShowInProgress = ShowInProgress,
            AutoEndDate = AutoEndDate,
            SelectionBackLight = SelectionBackLight,
            SelectionTextLight = SelectionTextLight,
            SelectionBackDark = SelectionBackDark,
            SelectionTextDark = SelectionTextDark,
            MilestoneTypes = MilestoneTypes.Select(t => t.ToModel()).ToList()
        }
    };

    private void Save()
    {
        Error = null;
        if (!Validate()) return;
        try
        {
            _entry.Info.Name = Name.Trim();
            _entry.Info.Description = (Description ?? "").Trim();
            _entry.Info.Color = Color;
            _entry.Info.Icon = HasIcon ? Icon : null;
            _entry.Info.Settings = BuildSettings();
            _projects.SaveInfo(_entry.Folder, _entry.Info);
            _projects.SaveAccounts(_entry.Folder, _store);
            _entry.MemberCount = _store.Accounts.Count;
            Saved = true;
            Close(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>
/// Shown when the logged-in account is not authorized in the project being opened:
/// login as a project member, join with the current account, or create a new account.
/// </summary>
public class ProjectAccessViewModel : DialogViewModel
{
    private readonly ProjectEntry _entry;
    private readonly AccountStore _store;
    private readonly ProjectService _projects;
    private readonly AccountService _accounts;
    private readonly IDialogService _dialogs;

    public ProjectAccessViewModel(ProjectEntry entry, AccountStore store, ProjectService projects,
        AccountService accounts, IDialogService dialogs)
    {
        _entry = entry;
        _store = store;
        _projects = projects;
        _accounts = accounts;
        _dialogs = dialogs;
        Members = store.Accounts.ToList();
        // Prefer a member whose account already exists on this PC.
        _selectedMember = Members.FirstOrDefault(m => accounts.FindById(m.Id) != null) ?? Members.FirstOrDefault();

        LoginCommand = new RelayCommand(Login);
        JoinCommand = new RelayCommand(Join);
        CreateCommand = new RelayCommand(CreateAccount);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string ProjectName => _entry.Info.Name;
    public string Message =>
        $"The account '{_accounts.Current?.Username}' is not among the authorized users of '{_entry.Info.Name}'.";
    public string JoinText => $"Join as '{_accounts.Current?.Username}'";
    public IReadOnlyList<Account> Members { get; }
    public bool HasMembers => Members.Count > 0;

    private Account? _selectedMember;
    public Account? SelectedMember { get => _selectedMember; set => SetProperty(ref _selectedMember, value); }

    private string _password = "";
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    public ICommand LoginCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    private void Login()
    {
        Error = null;
        if (SelectedMember is null)
        {
            Error = "Select a project user.";
            return;
        }
        if (!PasswordHasher.Verify(SelectedMember, Password))
        {
            Error = "Wrong password.";
            return;
        }
        // The project account becomes (or already is) an account of this PC, then it is logged in.
        var local = _accounts.Import(SelectedMember);
        _accounts.StartSession(local);
        Close(true);
    }

    private void Join()
    {
        var current = _accounts.Current;
        if (current is null) return;
        try
        {
            _store.Accounts.Add(current.CloneForProject());
            _projects.SaveAccounts(_entry.Folder, _store);
            Close(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void CreateAccount()
    {
        var vm = new AccountEditorViewModel(_accounts, null, _dialogs);
        if (_dialogs.ShowDialog(vm) != true || vm.Result is null) return;
        try
        {
            _accounts.StartSession(vm.Result);
            _store.Accounts.Add(vm.Result.CloneForProject());
            _projects.SaveAccounts(_entry.Folder, _store);
            Close(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
