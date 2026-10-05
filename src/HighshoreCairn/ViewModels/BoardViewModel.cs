using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

/// <summary>The three areas of an open project.</summary>
public enum ProjectMode { Kanban, Whiteboard, Docs }

/// <summary>
/// An open project: the Kanban board (columns, cards, filters, auto-save) plus the whiteboard,
/// the documentation and the tomato timer, which are created the first time they are needed.
/// </summary>
public class BoardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, CardViewModel> _cardCache = new();
    private bool _refreshing;

    public BoardViewModel(MainViewModel main, ProjectEntry project)
    {
        _main = main;
        _projects = main.Projects;
        _dialogs = main.Dialogs;
        Project = project;

        Filter = new FilterViewModel();
        Filter.Changed += RefreshCards;

        BackCommand = new RelayCommand(() => _main.ShowProjects());
        AddColumnCommand = new RelayCommand(AddColumn);
        ManageTagsCommand = new RelayCommand(() => ManageTags());
        ArchiveCommand = new RelayCommand(ShowArchive);
        SettingsCommand = new RelayCommand(() => OpenSettings());
        RoadmapCommand = new RelayCommand(ShowRoadmap);
        ClearFiltersCommand = new RelayCommand(() => { SelectedSavedFilter = null; Filter.Clear(); });
        SaveFilterCommand = new RelayCommand(SaveFilter);
        DeleteSavedFilterCommand = new RelayCommand(DeleteSavedFilter);
        ShowOverdueCommand = new RelayCommand(() => { Filter.Clear(); Filter.Due = DueFilter.Overdue; });
        ShowKanbanCommand = new RelayCommand(() => Mode = ProjectMode.Kanban);
        ShowWhiteboardCommand = new RelayCommand(() => Mode = ProjectMode.Whiteboard);
        ShowDocsCommand = new RelayCommand(() => Mode = ProjectMode.Docs);

        Pomodoro = new PomodoroViewModel(project.Info.Settings.Pomodoro, main.Sound);
        Pomodoro.Finished += message => StatusText = message;

        Data = new BoardData();
        Load(backup: true);
    }

    public ProjectEntry Project { get; }
    public BoardData Data { get; private set; }
    public List<Account> Members { get; private set; } = new();
    public Account CurrentUser => _main.Accounts.Current!;
    public FilterViewModel Filter { get; }
    public IDialogService Dialogs => _dialogs;
    public ObservableCollection<ColumnViewModel> Columns { get; } = new();
    public ObservableCollection<SavedFilter> SavedFilters { get; } = new();

    public string ProjectName => Project.Info.Name;
    public string ProjectDescription => Project.Info.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Project.Info.Description);
    public string ProjectColor => Project.Info.Color;
    /// <summary>Optional square picture of the project (base64 PNG), shown instead of the color bar.</summary>
    public string? ProjectIcon => Project.Info.Icon;
    public bool HasProjectIcon => !string.IsNullOrEmpty(Project.Info.Icon);
    public bool HasNoProjectIcon => !HasProjectIcon;

    /// <summary>The tomato timer shown in the header (shared by the three areas of the project).</summary>
    public PomodoroViewModel Pomodoro { get; }

    /// <summary>True while the dark theme is active: the columns have separate colors for the two themes.</summary>
    public bool IsDark => _main.IsDarkMode;

    /// <summary>Called by the main window when the theme changes.</summary>
    public void OnThemeChanged()
    {
        foreach (var column in Columns) column.NotifyChanged();
    }

    /// <summary>Called when the project is closed: stops the timer and saves the open documents.</summary>
    public void Close()
    {
        Pomodoro.Dispose();
        _docs?.Shutdown();
        _roadmap?.RequestClose();
    }

    public ICommand BackCommand { get; }
    public ICommand AddColumnCommand { get; }
    public ICommand ManageTagsCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand RoadmapCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand SaveFilterCommand { get; }
    public ICommand DeleteSavedFilterCommand { get; }
    public ICommand ShowOverdueCommand { get; }
    public ICommand ShowKanbanCommand { get; }
    public ICommand ShowWhiteboardCommand { get; }
    public ICommand ShowDocsCommand { get; }

    /// <summary>Lookup tables (card by id, subtasks, progress) for the current state of the board.</summary>
    public BoardIndex Index { get; private set; } = new(new BoardData());

    // ------------------------------------------------------------------ Kanban / Whiteboard / Documentation

    private ProjectMode _mode = ProjectMode.Kanban;
    /// <summary>The area of the project shown on screen.</summary>
    public ProjectMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            if (value == ProjectMode.Whiteboard && _whiteboard is null)
            {
                try
                {
                    _whiteboard = new WhiteboardViewModel(Project.Folder, _dialogs, this);
                }
                catch (Exception ex)
                {
                    _dialogs.Error("The whiteboard could not be opened.\n\n" + ex.Message);
                    value = _mode;
                }
                OnPropertyChanged(nameof(Whiteboard));
            }
            if (value == ProjectMode.Docs && _docs is null)
            {
                try
                {
                    _docs = new DocsViewModel(Project.Folder, _dialogs, () => Project.Info.Settings.Docs, _main.DocConverter);
                    _docs.PathChanged += OnDocumentPathChanged;
                    _docs.PathRemoved += OnDocumentRemoved;
                }
                catch (Exception ex)
                {
                    _dialogs.Error("The documentation could not be opened.\n\n" + ex.Message);
                    value = _mode;
                }
                OnPropertyChanged(nameof(Docs));
            }

            // Leaving the documentation: everything typed so far is written to disk.
            if (_mode == ProjectMode.Docs && value != ProjectMode.Docs) _docs?.SaveAll();
            if (value == ProjectMode.Docs) _docs?.CheckExternalChanges();
            // Back on the board: the references shown on the cards may have changed in the other areas.
            if (value == ProjectMode.Kanban && _mode != ProjectMode.Kanban)
            {
                _whiteboardLinks = null;
                RefreshCards();
            }
            // Back on the whiteboard: tasks and documents may have been renamed or deleted meanwhile.
            if (value == ProjectMode.Whiteboard && _mode != ProjectMode.Whiteboard)
            {
                _documents = null;
                _whiteboard?.RefreshGraphRefs();
            }

            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWhiteboardMode));
            OnPropertyChanged(nameof(IsKanbanMode));
            OnPropertyChanged(nameof(IsDocsMode));
        }
    }

    /// <summary>Shortcut kept for the whiteboard switch: true = whiteboard, false = Kanban board.</summary>
    public bool IsWhiteboardMode
    {
        get => _mode == ProjectMode.Whiteboard;
        set => Mode = value ? ProjectMode.Whiteboard : ProjectMode.Kanban;
    }

    public bool IsKanbanMode => _mode == ProjectMode.Kanban;
    public bool IsDocsMode => _mode == ProjectMode.Docs;

    private WhiteboardViewModel? _whiteboard;
    /// <summary>Created the first time the whiteboard is shown.</summary>
    public WhiteboardViewModel? Whiteboard => _whiteboard;

    private DocsViewModel? _docs;
    /// <summary>Created the first time the documentation is shown.</summary>
    public DocsViewModel? Docs => _docs;

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _dueSummary = "";
    /// <summary>Due-date notification shown in the board header ("2 cards overdue ...").</summary>
    public string DueSummary { get => _dueSummary; private set => SetProperty(ref _dueSummary, value); }

    private bool _hasOverdue;
    public bool HasOverdue { get => _hasOverdue; private set => SetProperty(ref _hasOverdue, value); }

    public string ArchiveText => Data.Archived.Count == 0 ? "Archive" : $"Archive ({Data.Archived.Count})";

    private SavedFilter? _selectedSavedFilter;
    public SavedFilter? SelectedSavedFilter
    {
        get => _selectedSavedFilter;
        set
        {
            if (!SetProperty(ref _selectedSavedFilter, value)) return;
            OnPropertyChanged(nameof(HasSelectedSavedFilter));
            if (value != null) Filter.Apply(value);
        }
    }

    public bool HasSelectedSavedFilter => _selectedSavedFilter != null;
    public bool HasSavedFilters => SavedFilters.Count > 0;

    // ------------------------------------------------------------------ lookups

    public Account? FindMember(string? id) => id is null ? null : Members.FirstOrDefault(m => m.Id == id);

    /// <summary>null for "nobody"; the username; or a placeholder when the user left the project.</summary>
    public string? MemberName(string? id) => id is null ? null : FindMember(id)?.Username ?? "(removed user)";

    public TagDef? FindTag(string id) => Data.Tags.FirstOrDefault(t => t.Id == id);

    // ------------------------------------------------------------------ load / save

    /// <summary>(Re)loads board and members from the project folder.</summary>
    public void Load(bool backup = false)
    {
        Data = _projects.LoadBoard(Project.Folder);
        if (Data.Columns.Count == 0 && Data.Archived.Count == 0 && Data.Tags.Count == 0)
            Data = BoardData.CreateDefault();
        Members = _projects.LoadAccounts(Project.Folder).Accounts;
        if (backup) TryBackup();

        _cardCache.Clear();
        _whiteboardLinks = null;
        SavedFilters.Clear();
        foreach (var f in Data.SavedFilters) SavedFilters.Add(f);
        _selectedSavedFilter = null;

        RebuildColumns();
        StatusText = $"Loaded {DateTime.Now:HH:mm:ss}";
        RaiseAllChanged();
    }

    /// <summary>Re-reads the authorized accounts (after a profile or membership change).</summary>
    public void ReloadMembers()
    {
        Members = _projects.LoadAccounts(Project.Folder).Accounts;
        RebuildColumns();
    }

    /// <summary>Auto-save: called after every change.</summary>
    public void Save()
    {
        try
        {
            _projects.SaveBoard(Project.Folder, Data);
            StatusText = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = "SAVE FAILED: " + ex.Message;
            _dialogs.Error("The board could not be saved.\n\n" + ex.Message);
        }
    }

    /// <summary>Automatic backup before a critical change (keeps the last 5).</summary>
    private void TryBackup()
    {
        try { _projects.Backup(Project.Folder); }
        catch { /* a failed backup must never block the user */ }
    }

    private ActivityEntry NewActivity(string text) => new()
    {
        At = DateTime.UtcNow,
        UserId = CurrentUser.Id,
        UserName = CurrentUser.Username,
        Text = text
    };

    /// <summary>Appends an entry to the card activity log, signed by the logged-in user.</summary>
    public void Log(CardData card, string text)
    {
        card.Activity.Add(NewActivity(text));
        card.UpdatedAt = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------ view refresh

    /// <summary>Recreates the column ViewModels from the data (after add/remove/reorder/rename).</summary>
    private void RebuildColumns()
    {
        var existing = Columns.ToDictionary(c => c.Data);
        Columns.Clear();
        foreach (var data in Data.Columns)
            Columns.Add(existing.TryGetValue(data, out var vm) ? vm : new ColumnViewModel(this, data));

        _refreshing = true;
        try { Filter.SetOptions(Members, Data.Tags, Data.Columns); }
        finally { _refreshing = false; }
        RefreshCards();
    }

    /// <summary>Recomputes the visible cards of every column (filters + sort) and the due summary.</summary>
    public void RefreshCards()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            Index = new BoardIndex(Data);
            _documents = null;
            var now = DateTime.Now;
            var alive = new HashSet<string>();
            foreach (var column in Columns)
            {
                column.IsVisible = Filter.ColumnVisible(column.Data.Id);
                var matching = column.Data.Cards.Where(c => Filter.Matches(c, column.Data.IsDone, now));
                var sorted = Filter.SortCards(matching, MemberName);

                column.VisibleCards.Clear();
                foreach (var data in sorted)
                {
                    if (!_cardCache.TryGetValue(data.Id, out var vm) || !ReferenceEquals(vm.Data, data))
                    {
                        vm = new CardViewModel(this, data, column);
                        _cardCache[data.Id] = vm;
                    }
                    vm.Column = column;
                    vm.DropAbove = vm.DropBelow = false;
                    vm.RaiseAllChanged();
                    column.VisibleCards.Add(vm);
                }
                foreach (var data in column.Data.Cards) alive.Add(data.Id);
                column.NotifyChanged();
            }

            foreach (var id in _cardCache.Keys.Where(k => !alive.Contains(k)).ToList()) _cardCache.Remove(id);
            UpdateDueSummary(now);
            OnPropertyChanged(nameof(ArchiveText));
        }
        finally
        {
            _refreshing = false;
        }
        Changed?.Invoke();
    }

    /// <summary>The board changed (cards, tags, columns): the roadmap window redraws itself.</summary>
    public event Action? Changed;

    private void UpdateDueSummary(DateTime now)
    {
        int overdue = 0, mine = 0, week = 0;
        foreach (var column in Data.Columns)
        {
            foreach (var card in column.Cards)
            {
                if (FilterViewModel.IsOverdue(card, column.IsDone, now))
                {
                    overdue++;
                    if (card.AssigneeId == CurrentUser.Id) mine++;
                }
                else if (!column.IsDone && FilterViewModel.MatchesDue(card, DueFilter.ThisWeek, false, now))
                {
                    week++;
                }
            }
        }

        HasOverdue = overdue > 0;
        var parts = new List<string>();
        if (overdue > 0)
            parts.Add($"{overdue} overdue" + (mine > 0 ? $" ({mine} assigned to you)" : ""));
        if (week > 0) parts.Add($"{week} due this week");
        DueSummary = string.Join("  ·  ", parts);
    }

    private void Commit()
    {
        Save();
        RefreshCards();
    }

    // ------------------------------------------------------------------ columns

    private void AddColumn()
    {
        var name = _dialogs.Prompt("New column", "Column name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        Data.Columns.Add(new ColumnData { Name = name });
        Save();
        RebuildColumns();
    }

    public void RenameColumn(ColumnViewModel column)
    {
        var name = _dialogs.Prompt("Rename column", "Column name:", column.Name);
        if (string.IsNullOrWhiteSpace(name) || name == column.Name) return;
        column.Data.Name = name;
        Save();
        RebuildColumns();
    }

    public void DeleteColumn(ColumnViewModel column)
    {
        if (Data.Columns.Count <= 1)
        {
            _dialogs.Info("A board needs at least one column.");
            return;
        }
        var count = column.Data.Cards.Count;
        var message = count == 0
            ? $"Delete the column '{column.Name}'?"
            : $"Delete the column '{column.Name}' and its {count} card(s)?\n\nA backup of the board is created first.";
        if (!_dialogs.Confirm(message, "Delete column")) return;

        if (count > 0) TryBackup();
        var deleted = column.Data.Cards.Select(c => c.Id).ToList();
        Data.Columns.Remove(column.Data);
        ClearDanglingParents();
        Save();
        ForgetDeletedCards(deleted);
        RebuildColumns();
    }

    public void MoveColumn(ColumnViewModel column, int delta)
    {
        var index = Data.Columns.IndexOf(column.Data);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Data.Columns.Count) return;
        Data.Columns.RemoveAt(index);
        Data.Columns.Insert(target, column.Data);
        Save();
        RebuildColumns();
    }

    /// <summary>Called when a column flag (IsDone) changes.</summary>
    public void OnColumnChanged() => Commit();

    /// <summary>Opens the dialog to change the background and title colors of a column.</summary>
    public void EditColumnStyle(ColumnViewModel column)
    {
        var vm = new ColumnStyleViewModel(column.Data, IsDark);
        if (_dialogs.ShowDialog(vm) != true) return;
        column.Data.Background = vm.Light.ResultBackground;
        column.Data.TitleColor = vm.Light.ResultTitleColor;
        column.Data.BackgroundDark = vm.Dark.ResultBackground;
        column.Data.TitleColorDark = vm.Dark.ResultTitleColor;
        Commit();
    }

    // ------------------------------------------------------------------ cards

    public void AddCard(ColumnViewModel column)
    {
        var draft = new CardData
        {
            CreatorId = CurrentUser.Id,
            CreatorName = CurrentUser.Username
        };
        var editor = new CardEditorViewModel(this, draft, column, isNew: true);
        if (_dialogs.ShowDialog(editor) != true || editor.Result is null) return;

        var target = editor.SelectedColumn ?? column;
        target.Data.Cards.Add(editor.Result);
        ApplySubtasks(editor.Result, target.Data, editor.ResultSubtaskIds, editor.NewSubtaskTitles);
        Commit();
    }

    public void EditCard(CardViewModel card)
    {
        var editor = new CardEditorViewModel(this, card.Data, card.Column, isNew: false);
        var result = _dialogs.ShowDialog(editor);
        if (result != true || editor.Result is null)
        {
            // Tags may have been edited from inside the dialog even if the card was not saved.
            RefreshCards();
            return;
        }

        card.Data.CopyFrom(editor.Result);
        var target = editor.SelectedColumn ?? card.Column;
        if (target != card.Column)
        {
            card.Column.Data.Cards.Remove(card.Data);
            target.Data.Cards.Add(card.Data);
            Log(card.Data, $"moved this card from {card.Column.Name} to {target.Name}");
            StampEndDate(card.Data, target.Data);
        }
        ApplySubtasks(card.Data, target.Data, editor.ResultSubtaskIds, editor.NewSubtaskTitles);
        Commit();
    }

    /// <summary>
    /// A card that enters a "done" column without an end date gets the current date and time
    /// (option "Set the end date automatically" of the roadmap settings).
    /// </summary>
    private void StampEndDate(CardData card, ColumnData target)
    {
        if (!target.IsDone || card.EndDate != null || !Project.Info.Settings.Roadmap.AutoEndDate) return;
        var now = DateTime.Now;
        card.EndDate = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Unspecified);
        card.EndHasTime = true;
        Log(card, $"end date set to {CardViewModel.FormatDue(card.EndDate, true)} (moved to a done column)");
    }

    // ------------------------------------------------------------------ references & navigation

    private DocsService? _docFiles;
    private DocsService DocFiles => _docFiles ??= new DocsService(Project.Folder);

    private List<DocNode>? _documents;
    /// <summary>The files of the documentation (cached until the next refresh of the board).</summary>
    private List<DocNode> Documents
    {
        get
        {
            if (_documents != null) return _documents;
            try { _documents = DocFiles.Files(); }
            catch { _documents = new List<DocNode>(); }
            return _documents;
        }
    }

    /// <summary>The documents that can be linked to a task or to a whiteboard element.</summary>
    public IReadOnlyList<PickItem> DocumentChoices()
    {
        _documents = null; // the documentation may have changed since the last look
        return Documents.OrderBy(d => d.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new PickItem(d.Path, d.Title, d.DisplayPath)).ToList();
    }

    /// <summary>Title of a referenced document, and whether it still exists.</summary>
    public ReferenceRow ReferenceRow(string path)
    {
        var node = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return node != null
            ? new ReferenceRow(path, node.Title, false)
            : new ReferenceRow(path, DocsService.TitleOf(DocsService.FileNameOf(path)), true);
    }

    /// <summary>The tasks on the board, for the pickers ("Title", "Column").</summary>
    public IReadOnlyList<PickItem> CardChoices() =>
        Data.Columns.SelectMany(c => c.Cards.Select(card => new PickItem(card.Id, card.Title, c.Name))).ToList();

    /// <summary>Title of a task by id; null when it is not on the board (deleted or archived).</summary>
    public string? CardTitle(string cardId) => Index.Find(cardId)?.Title;

    public bool IsArchivedCard(string cardId) => Data.Archived.Any(c => c.Id == cardId);

    /// <summary>Title of an archived task; null when the task is not in the archive.</summary>
    public string? ArchivedCardTitle(string cardId) => Data.Archived.FirstOrDefault(c => c.Id == cardId)?.Title;

    private Dictionary<string, WbCardLink>? _whiteboardLinks;
    /// <summary>The whiteboard element that references a task, if any (a task has one at most).</summary>
    public WbCardLink? WhiteboardLinkOf(string cardId)
    {
        if (_whiteboardLinks is null)
        {
            try
            {
                _whiteboardLinks = WhiteboardLinks.CardIndex(_whiteboard?.Service ?? new WhiteboardService(Project.Folder), _whiteboard?.Data);
            }
            catch { _whiteboardLinks = new Dictionary<string, WbCardLink>(); }
        }
        return _whiteboardLinks.GetValueOrDefault(cardId);
    }

    /// <summary>The references of the whiteboard changed: the cards show them again from scratch.</summary>
    public void OnWhiteboardLinksChanged()
    {
        _whiteboardLinks = null;
        if (_mode == ProjectMode.Kanban) RefreshCards();
    }

    /// <summary>A document or folder was renamed / moved / converted: the references follow it.</summary>
    private void OnDocumentPathChanged(string oldPath, string newPath)
    {
        try
        {
            if (WhiteboardLinks.RemapDocs(Data, oldPath, newPath)) Save();
            var service = _whiteboard?.Service ?? new WhiteboardService(Project.Folder);
            _whiteboard?.RemapDocs(oldPath, newPath);
            WhiteboardLinks.RemapDocsOnDisk(service, oldPath, newPath, _whiteboard?.Data.Id);
        }
        catch (Exception ex)
        {
            StatusText = "References not updated: " + ex.Message;
        }
        _documents = null;
    }

    /// <summary>A document or folder was deleted: tasks and whiteboard elements forget it.</summary>
    private void OnDocumentRemoved(string path)
    {
        try
        {
            if (WhiteboardLinks.RemoveDocs(Data, path)) Save();
            var service = _whiteboard?.Service ?? new WhiteboardService(Project.Folder);
            _whiteboard?.RemoveDocs(path);
            WhiteboardLinks.RemoveDocsOnDisk(service, path, _whiteboard?.Data.Id);
        }
        catch (Exception ex)
        {
            StatusText = "References not updated: " + ex.Message;
        }
        _documents = null;
    }

    private CardViewModel? _highlighted;

    /// <summary>Removes the mark left by <see cref="GoToCard"/>.</summary>
    public void ClearHighlight()
    {
        if (_highlighted is null) return;
        _highlighted.IsHighlighted = false;
        _highlighted = null;
    }

    /// <summary>Asks the view to scroll a card into view.</summary>
    public event Action<CardViewModel>? CardFocusRequested;

    /// <summary>Shows the Kanban board with the task marked (not opened). False when the task is not on the board.</summary>
    public bool GoToCard(string cardId)
    {
        if (Index.Find(cardId) is null)
        {
            _dialogs.Info("This task is no longer on the board (it was deleted or archived).", "Reference");
            return false;
        }
        Mode = ProjectMode.Kanban;
        if (!_cardCache.TryGetValue(cardId, out var card) || !card.Column.IsVisible || !card.Column.VisibleCards.Contains(card))
        {
            // Hidden by a filter (of the cards or of the columns): show everything.
            SelectedSavedFilter = null;
            Filter.Clear();
            RefreshCards();
            if (!_cardCache.TryGetValue(cardId, out card)) return false;
        }
        ClearHighlight();
        card.IsHighlighted = true;
        _highlighted = card;
        CardFocusRequested?.Invoke(card);
        return true;
    }

    /// <summary>Opens the Documentation area on a document.</summary>
    public bool GoToDocument(string path)
    {
        if (!File.Exists(DocFiles.FullPath(path)))
        {
            _dialogs.Info($"The document '{path}' no longer exists.", "Reference");
            return false;
        }
        Mode = ProjectMode.Docs;
        _docs?.Open(path);
        return _docs != null;
    }

    /// <summary>Opens the whiteboard with the element selected and centered.</summary>
    public bool GoToWhiteboardElement(string boardId, string elementId)
    {
        Mode = ProjectMode.Whiteboard;
        return _whiteboard?.ShowElement(boardId, elementId) == true;
    }

    /// <summary>Opens the card dialog of a task by id (used by the roadmap).</summary>
    public void EditCardById(string cardId)
    {
        if (!_cardCache.TryGetValue(cardId, out var card))
        {
            var data = Index.Find(cardId);
            var column = data is null ? null : Columns.FirstOrDefault(c => c.Data.Cards.Contains(data));
            if (data is null || column is null) return;
            card = new CardViewModel(this, data, column);
        }
        EditCard(card);
    }

    /// <summary>Removes the two roadmap-only dates of a task: it leaves the roadmap, nothing else changes.</summary>
    public void ClearRoadmapDates(string cardId)
    {
        var card = Index.Find(cardId);
        if (card is null || (card.StartDate is null && card.FinishDate is null)) return;
        card.StartDate = null;
        card.FinishDate = null;
        Log(card, "removed the start and finish dates (roadmap)");
        Commit();
    }

    // ------------------------------------------------------------------ roadmap

    private RoadmapViewModel? _roadmap;
    /// <summary>The roadmap window of the project (created the first time it is opened).</summary>
    public RoadmapViewModel? Roadmap => _roadmap;

    private void ShowRoadmap()
    {
        try
        {
            _roadmap ??= new RoadmapViewModel(this, _dialogs);
            _roadmap.Refresh();
            _dialogs.ShowWindow(_roadmap);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The roadmap could not be opened.\n\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ dependencies (subtasks)

    /// <summary>Makes <paramref name="card"/> a subtask of another card (null = top-level task).</summary>
    public void SetParent(CardViewModel card, string? parentId)
    {
        if (card.Data.ParentId == parentId) return;
        if (parentId != null)
        {
            var parent = Index.Find(parentId);
            if (parent is null || !Index.CanBeChildOf(card.Data.Id, parentId)) return;
            Log(card.Data, $"became a subtask of \"{parent.Title}\"");
            Log(parent, $"got the subtask \"{card.Data.Title}\"");
        }
        else
        {
            Log(card.Data, "is no longer a subtask");
        }
        card.Data.ParentId = parentId;
        Commit();
    }

    /// <summary>
    /// Aligns the subtasks of <paramref name="parent"/> with the list chosen in the card dialog:
    /// links/unlinks existing cards and creates the new ones in <paramref name="column"/>.
    /// </summary>
    private void ApplySubtasks(CardData parent, ColumnData column, IReadOnlyCollection<string> subtaskIds, IReadOnlyList<string> newTitles)
    {
        var index = new BoardIndex(Data);

        foreach (var child in index.ChildrenOf(parent.Id).ToList())
        {
            if (subtaskIds.Contains(child.Id)) continue;
            child.ParentId = null;
            Log(child, $"is no longer a subtask of \"{parent.Title}\"");
            Log(parent, $"removed the subtask \"{child.Title}\"");
        }

        foreach (var id in subtaskIds)
        {
            var child = index.Find(id);
            if (child is null || child.ParentId == parent.Id || !index.CanBeChildOf(id, parent.Id)) continue;
            child.ParentId = parent.Id;
            Log(child, $"became a subtask of \"{parent.Title}\"");
            Log(parent, $"got the subtask \"{child.Title}\"");
        }

        foreach (var title in newTitles)
        {
            var child = new CardData
            {
                Title = title,
                ParentId = parent.Id,
                CreatorId = CurrentUser.Id,
                CreatorName = CurrentUser.Username
            };
            Log(child, $"created this card as a subtask of \"{parent.Title}\"");
            column.Cards.Add(child);
            Log(parent, $"got the subtask \"{title}\"");
        }
    }

    /// <summary>Clears the parent link of cards whose parent no longer exists (deleted).</summary>
    private void ClearDanglingParents()
    {
        var existing = Data.Columns.SelectMany(c => c.Cards).Concat(Data.Archived).Select(c => c.Id).ToHashSet();
        foreach (var card in Data.Columns.SelectMany(c => c.Cards).Concat(Data.Archived))
            if (card.ParentId != null && !existing.Contains(card.ParentId)) card.ParentId = null;
    }

    public void DeleteCard(CardViewModel card)
    {
        if (!_dialogs.Confirm(
                $"Permanently delete the card '{card.Title}'?\n\nA backup of the board is created first. " +
                "Use Archive to hide a card without deleting it.", "Delete card")) return;
        TryBackup();
        card.Column.Data.Cards.Remove(card.Data);
        ClearDanglingParents();
        ForgetDeletedCards(new[] { card.Data.Id });
        Commit();
    }

    /// <summary>
    /// Cards deleted for good: the whiteboard elements that pointed to them forget them (an archived
    /// card keeps its link, so that restoring it brings everything back).
    /// </summary>
    private void ForgetDeletedCards(ICollection<string> cardIds)
    {
        if (cardIds.Count == 0) return;
        try
        {
            _whiteboard?.ForgetCards(cardIds);
            WhiteboardLinks.UnlinkCardsOnDisk(_whiteboard?.Service ?? new WhiteboardService(Project.Folder), cardIds, _whiteboard?.Data.Id);
        }
        catch (Exception ex)
        {
            StatusText = "References not updated: " + ex.Message;
        }
        _whiteboardLinks = null;
    }

    public void ArchiveCard(CardViewModel card)
    {
        card.Column.Data.Cards.Remove(card.Data);
        card.Data.ArchivedFromColumnId = card.Column.Data.Id;
        card.Data.ArchivedAt = DateTime.UtcNow;
        Log(card.Data, $"archived this card (was in {card.Column.Name})");
        Data.Archived.Add(card.Data);
        Commit();
    }

    public void RestoreCard(CardData card)
    {
        if (!Data.Archived.Remove(card)) return;
        var column = Data.Columns.FirstOrDefault(c => c.Id == card.ArchivedFromColumnId) ?? Data.Columns.First();
        card.ArchivedFromColumnId = null;
        card.ArchivedAt = null;
        Log(card, $"restored this card to {column.Name}");
        column.Cards.Add(card);
        Commit();
    }

    public void DeleteArchivedCard(CardData card)
    {
        TryBackup();
        Data.Archived.Remove(card);
        ClearDanglingParents();
        ForgetDeletedCards(new[] { card.Id });
        Commit();
    }

    public void SetPriority(CardViewModel card, Priority priority)
    {
        if (card.Data.Priority == priority) return;
        Log(card.Data, $"changed priority from {card.Data.Priority} to {priority}");
        card.Data.Priority = priority;
        Commit();
    }

    public void SetAssignee(CardViewModel card, string? accountId)
    {
        if (card.Data.AssigneeId == accountId) return;
        card.Data.AssigneeId = accountId;
        Log(card.Data, accountId is null ? "removed the assignee" : $"assigned this card to {MemberName(accountId)}");
        Commit();
    }

    public void ToggleTag(CardViewModel card, string tagId)
    {
        var tag = FindTag(tagId);
        if (tag is null) return;
        if (card.Data.TagIds.Remove(tagId))
            Log(card.Data, $"removed the tag {tag.Name}");
        else
        {
            card.Data.TagIds.Add(tagId);
            Log(card.Data, $"added the tag {tag.Name}");
        }
        Commit();
    }

    public void MoveCardToColumn(CardViewModel card, ColumnViewModel target) =>
        MoveCard(card, target, null, after: false);

    /// <summary>
    /// Drag &amp; drop: moves a card to <paramref name="target"/>, placing it before/after
    /// <paramref name="anchor"/> (or at the end of the column when there is no anchor).
    /// </summary>
    public void MoveCard(CardViewModel card, ColumnViewModel target, CardViewModel? anchor, bool after)
    {
        if (anchor == card) return;
        var source = card.Column;
        if (source == target && anchor is null && source.Data.Cards.LastOrDefault() == card.Data) return;

        source.Data.Cards.Remove(card.Data);
        var index = anchor is null ? -1 : target.Data.Cards.IndexOf(anchor.Data);
        if (index < 0) index = target.Data.Cards.Count;
        else if (after) index++;
        target.Data.Cards.Insert(index, card.Data);

        if (source != target)
        {
            Log(card.Data, $"moved this card from {source.Name} to {target.Name}");
            StampEndDate(card.Data, target.Data);
        }
        Commit();
    }

    // ------------------------------------------------------------------ tags / archive / settings

    /// <summary>Opens the tag manager. Returns true when the tags changed.</summary>
    public bool ManageTags()
    {
        var vm = new TagManagerViewModel(this, _dialogs);
        if (_dialogs.ShowDialog(vm) != true) return false;
        Save();
        RebuildColumns();
        return true;
    }

    private void ShowArchive()
    {
        _dialogs.ShowDialog(new ArchiveViewModel(this, _dialogs));
        RefreshCards();
    }

    /// <param name="tab">The tab to show first: "General", "Users", "Backups", "Timer", "Documentation" or "Roadmap".</param>
    public void OpenSettings(string? tab = null)
    {
        var vm = new ProjectSettingsViewModel(Project, _projects, _main.Accounts, _dialogs, _main.Sound, _main.Icons, tab)
        {
            BeforeBackup = Save
        };
        _dialogs.ShowDialog(vm);
        if (vm.Restored)
            Load();
        else if (vm.Saved)
        {
            Members = _projects.LoadAccounts(Project.Folder).Accounts;
            RebuildColumns();
            RaiseAllChanged();
        }
        if (vm.Saved) Pomodoro.ApplySettings(Project.Info.Settings.Pomodoro);
        if (vm.Saved) _roadmap?.Refresh();
        _main.OnProjectChanged();
        if (vm.Saved && !vm.Restored) CheckHiddenMilestones();
    }

    /// <summary>The milestones of the roadmap that others of their level hide completely (see <see cref="MilestoneRules"/>).</summary>
    private List<Milestone> HiddenMilestones()
    {
        try
        {
            var data = _roadmap?.Data ?? new RoadmapService(Project.Folder).Load();
            return MilestoneRules.Hidden(data.Milestones, Project.Info.Settings.Roadmap);
        }
        catch { return new List<Milestone>(); }   // an unreadable roadmap file is reported when the roadmap opens
    }

    /// <summary>
    /// Changing the level of a milestone type (or removing a type) can put a milestone completely under
    /// others: same warning as when a milestone is edited. Yes deletes the covered ones, No reopens the settings.
    /// </summary>
    private void CheckHiddenMilestones()
    {
        var hidden = HiddenMilestones();
        if (hidden.Count == 0) return;

        var names = string.Join("\n", hidden.Select(m => $"  •  {m.Name}  ({m.Start:dd MMM yyyy} – {m.End:dd MMM yyyy})"));
        var proceed = _dialogs.Confirm(
            (hidden.Count == 1 ? "With the new settings this milestone is completely covered by others on the same line:"
                : "With the new settings these milestones are completely covered by others on the same line:") +
            $"\n\n{names}\n\nA covered milestone is not shown. Yes deletes " + (hidden.Count == 1 ? "it" : "them") +
            "; No goes back to the settings, where the levels of the milestone types can be changed.", "Roadmap");
        if (!proceed)
        {
            OpenSettings("Roadmap");   // (leaving that window with Cancel keeps things as they are: the milestone stays hidden)
            return;
        }

        var ids = hidden.Select(m => m.Id).ToHashSet();
        try
        {
            if (_roadmap != null) _roadmap.RemoveMilestones(ids);
            else
            {
                var service = new RoadmapService(Project.Folder);
                var data = service.Load();
                data.Milestones.RemoveAll(m => ids.Contains(m.Id));
                service.Save(data);
            }
        }
        catch (Exception ex) { _dialogs.Error("The roadmap could not be saved.\n\n" + ex.Message); }
    }

    // ------------------------------------------------------------------ saved filters

    private void SaveFilter()
    {
        var name = _dialogs.Prompt("Save filter", "Name of the filter:", SelectedSavedFilter?.Name ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;

        var saved = Filter.ToSaved(name);
        var existing = Data.SavedFilters.FindIndex(f => string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing >= 0)
        {
            saved.Id = Data.SavedFilters[existing].Id;
            Data.SavedFilters[existing] = saved;
            SavedFilters[existing] = saved;
        }
        else
        {
            Data.SavedFilters.Add(saved);
            SavedFilters.Add(saved);
        }
        Save();
        _selectedSavedFilter = saved;
        OnPropertyChanged(nameof(SelectedSavedFilter));
        OnPropertyChanged(nameof(HasSelectedSavedFilter));
        OnPropertyChanged(nameof(HasSavedFilters));
    }

    private void DeleteSavedFilter()
    {
        var saved = SelectedSavedFilter;
        if (saved is null) return;
        if (!_dialogs.Confirm($"Delete the saved filter '{saved.Name}'?", "Delete filter")) return;
        Data.SavedFilters.Remove(saved);
        SavedFilters.Remove(saved);
        SelectedSavedFilter = null;
        OnPropertyChanged(nameof(HasSavedFilters));
        Save();
    }
}

/// <summary>Row of the tag manager.</summary>
public class TagRow : ObservableObject
{
    public TagRow(string id, string name, string color)
    {
        Id = id;
        _name = name;
        _color = color;
    }

    public string Id { get; }

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _color;
    public string Color { get => _color; set => SetProperty(ref _color, value); }
}

/// <summary>Dialog to add, rename, recolor and delete the tags of the project.</summary>
public class TagManagerViewModel : DialogViewModel
{
    private readonly BoardViewModel _board;
    private readonly IDialogService _dialogs;

    public TagManagerViewModel(BoardViewModel board, IDialogService dialogs)
    {
        _board = board;
        _dialogs = dialogs;
        foreach (var tag in board.Data.Tags) Tags.Add(new TagRow(tag.Id, tag.Name, tag.Color));
        _selected = Tags.FirstOrDefault();

        AddCommand = new RelayCommand(() =>
        {
            var row = new TagRow(Guid.NewGuid().ToString("N"), "New tag", Palette.Random());
            Tags.Add(row);
            Selected = row;
        });
        RemoveCommand = new RelayCommand<TagRow>(row =>
        {
            row ??= Selected;
            if (row is null) return;
            Tags.Remove(row);
            Selected = Tags.FirstOrDefault();
        });
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public ObservableCollection<TagRow> Tags { get; } = new();
    public IReadOnlyList<string> Colors => Palette.Colors;

    private TagRow? _selected;
    public TagRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => _selected != null;

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void Save()
    {
        Error = null;
        if (Tags.Any(t => string.IsNullOrWhiteSpace(t.Name)))
        {
            Error = "Every tag needs a name.";
            return;
        }
        if (Tags.Any(t => !Palette.IsValidHex(t.Color)))
        {
            Error = "Tag colors must be hex values like #E5484D.";
            return;
        }

        var data = _board.Data;
        var removed = data.Tags.Where(t => Tags.All(r => r.Id != t.Id)).ToList();
        if (removed.Count > 0)
        {
            var used = data.Columns.SelectMany(c => c.Cards).Concat(data.Archived)
                .Count(card => card.TagIds.Any(id => removed.Any(r => r.Id == id)));
            if (used > 0 && !_dialogs.Confirm(
                    $"{removed.Count} tag(s) will be deleted and removed from {used} card(s). Continue?", "Delete tags"))
                return;
            var removedIds = removed.Select(r => r.Id).ToHashSet();
            foreach (var card in data.Columns.SelectMany(c => c.Cards).Concat(data.Archived))
                card.TagIds.RemoveAll(removedIds.Contains);
        }

        data.Tags = Tags.Select(r => new TagDef { Id = r.Id, Name = r.Name.Trim(), Color = r.Color }).ToList();
        Close(true);
    }
}

/// <summary>Row of the archive dialog.</summary>
public class ArchivedRow
{
    public ArchivedRow(CardData card, string columnName)
    {
        Card = card;
        Title = card.Title;
        Info = $"Archived {card.ArchivedAt?.ToLocalTime():yyyy-MM-dd HH:mm}  ·  from {columnName}";
    }

    public CardData Card { get; }
    public string Title { get; }
    public string Info { get; }
}

/// <summary>Dialog listing the archived cards: restore or delete permanently.</summary>
public class ArchiveViewModel : DialogViewModel
{
    private readonly BoardViewModel _board;
    private readonly IDialogService _dialogs;

    public ArchiveViewModel(BoardViewModel board, IDialogService dialogs)
    {
        _board = board;
        _dialogs = dialogs;
        RestoreCommand = new RelayCommand<ArchivedRow>(row =>
        {
            if (row is null) return;
            _board.RestoreCard(row.Card);
            Reload();
        });
        DeleteCommand = new RelayCommand<ArchivedRow>(row =>
        {
            if (row is null) return;
            if (!_dialogs.Confirm($"Permanently delete the archived card '{row.Title}'?", "Delete card")) return;
            _board.DeleteArchivedCard(row.Card);
            Reload();
        });
        CloseCommand = new RelayCommand(() => Close(true));
        Reload();
    }

    public ObservableCollection<ArchivedRow> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public ICommand RestoreCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CloseCommand { get; }

    private void Reload()
    {
        Items.Clear();
        foreach (var card in _board.Data.Archived.OrderByDescending(c => c.ArchivedAt))
        {
            var column = _board.Data.Columns.FirstOrDefault(c => c.Id == card.ArchivedFromColumnId)?.Name ?? "a deleted column";
            Items.Add(new ArchivedRow(card, column));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>The colors of a column for one theme (light or dark), edited in the column colors dialog.</summary>
public class ColumnThemeStyle : ObservableObject
{
    public ColumnThemeStyle(bool isDark, string? background, string? titleColor, bool isCurrent)
    {
        IsDark = isDark;
        IsCurrent = isCurrent;
        _background = Palette.IsValidHex(background) ? background : null;
        _titleColor = Palette.IsValidHex(titleColor) ? titleColor : null;
        ResetBackgroundCommand = new RelayCommand(() => Background = null);
        ResetTitleCommand = new RelayCommand(() => TitleColor = null);
    }

    public bool IsDark { get; }
    /// <summary>This is the theme in use right now.</summary>
    public bool IsCurrent { get; }
    public string Heading => (IsDark ? "Dark theme" : "Light theme") + (IsCurrent ? "  (in use)" : "");

    // The colors of the two themes (same values as Themes/Light.xaml and Themes/Dark.xaml): the preview
    // of each theme is always drawn with its own colors, whatever theme the app is using.
    public string PageColor => IsDark ? "#0F1720" : "#F4F6F8";
    public string DefaultBackground => IsDark ? "#1C2733" : "#E7ECF0";
    public string DefaultTitleColor => IsDark ? "#E6EDF3" : "#1B2733";
    public string CardColor => IsDark ? "#18222D" : "#FFFFFF";
    public string CardBorderColor => IsDark ? "#2E3B49" : "#D5DBE1";

    public string PreviewBackground => HasBackground ? _background! : DefaultBackground;
    public string PreviewTitleColor => HasTitleColor ? _titleColor! : DefaultTitleColor;

    /// <summary>Soft tints for the light theme, deep tones for the dark one.</summary>
    public IReadOnlyList<string> BackgroundColors => IsDark
        ? new[] { "#3A1F22", "#3D3018", "#3A3A14", "#2A3518", "#17382C", "#17384A", "#1E2A4A", "#2E2447", "#3A1F33", "#26323E", "#101820", "#2B2B2B" }
        : new[] { "#FDECEC", "#FDF1DC", "#FFF7CC", "#E6F4D7", "#DDF3EA", "#E3F0F4", "#E1E9FF", "#EFE5FA", "#FBE4F1", "#ECEFF2", "#FFFFFF", "#D9DEE3" };

    public IReadOnlyList<string> TitleColors => IsDark
        ? new[] { "#FFFFFF", "#F28B8B", "#F2B866", "#F5E06B", "#A6D96A", "#5ED3A5", "#6CC5E0", "#8FB0FF", "#C3A6F5", "#F29BCB", "#C5CCD3", "#93A1AF" }
        : new[] { "#000000", "#B42318", "#9A5B00", "#7A6A00", "#3F7D1D", "#1B7F5C", "#1F6F8B", "#16324F", "#5B5BD6", "#8E4EC6", "#D6409F", "#5F6B7A" };

    private string? _background;
    /// <summary>null = default background of the theme.</summary>
    public string? Background
    {
        get => _background;
        set
        {
            if (!SetProperty(ref _background, value)) return;
            OnPropertyChanged(nameof(BackgroundHex));
            OnPropertyChanged(nameof(HasBackground));
            OnPropertyChanged(nameof(PreviewBackground));
        }
    }

    public string BackgroundHex
    {
        get => _background ?? "";
        set => Background = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool HasBackground => Palette.IsValidHex(_background);

    private string? _titleColor;
    /// <summary>null = default text color of the theme (black on the light theme).</summary>
    public string? TitleColor
    {
        get => _titleColor;
        set
        {
            if (!SetProperty(ref _titleColor, value)) return;
            OnPropertyChanged(nameof(TitleHex));
            OnPropertyChanged(nameof(HasTitleColor));
            OnPropertyChanged(nameof(PreviewTitleColor));
        }
    }

    public string TitleHex
    {
        get => _titleColor ?? "";
        set => TitleColor = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool HasTitleColor => Palette.IsValidHex(_titleColor);

    public bool IsValid => (_background is null || HasBackground) && (_titleColor is null || HasTitleColor);

    public string? ResultBackground => HasBackground ? _background : null;
    public string? ResultTitleColor => HasTitleColor ? _titleColor : null;

    public ICommand ResetBackgroundCommand { get; }
    public ICommand ResetTitleCommand { get; }
}

/// <summary>
/// Dialog to pick the background color and the name color of a column, separately for the light
/// and for the dark theme (or to go back to the defaults).
/// </summary>
public class ColumnStyleViewModel : DialogViewModel
{
    public ColumnStyleViewModel(ColumnData column, bool darkInUse = false)
    {
        ColumnName = column.Name;
        Light = new ColumnThemeStyle(false, column.Background, column.TitleColor, !darkInUse);
        Dark = new ColumnThemeStyle(true, column.BackgroundDark, column.TitleColorDark, darkInUse);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string ColumnName { get; }
    public ColumnThemeStyle Light { get; }
    public ColumnThemeStyle Dark { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void Save()
    {
        Error = null;
        if (!Light.IsValid || !Dark.IsValid)
        {
            Error = "Colors must be hex values like #E3F0F4 (leave empty for the default).";
            return;
        }
        Close(true);
    }
}
