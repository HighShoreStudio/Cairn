using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

public class AssigneeOption
{
    public AssigneeOption(string? id, string name, string color, string? image = null)
    {
        Id = id;
        Name = name;
        Color = color;
        Image = image;
    }

    public string? Image { get; }

    public string? Id { get; }
    public string Name { get; }
    public string Color { get; }
    public string Initials => Id is null ? "–" : Account.MakeInitials(Name);
}

/// <summary>A card that can be picked as parent task or as subtask.</summary>
public class CardOption
{
    public CardOption(string? id, string label)
    {
        Id = id;
        Label = label;
    }

    public string? Id { get; }
    public string Label { get; }
}

/// <summary>Row of the subtask list in the card dialog.</summary>
public class SubtaskRow
{
    public SubtaskRow(string? id, string title, string info, double progress)
    {
        Id = id;
        Title = title;
        Info = info;
        Progress = progress;
    }

    /// <summary>null for a subtask typed in the dialog that will be created on Save.</summary>
    public string? Id { get; }
    public string Title { get; }
    public string Info { get; }
    public double Progress { get; }
    public bool IsNew => Id is null;
}

/// <summary>A document linked to a card (section "References" of the card dialog).</summary>
public class ReferenceRow
{
    public ReferenceRow(string path, string title, bool isMissing)
    {
        Path = path;
        Title = title;
        IsMissing = isMissing;
    }

    /// <summary>Path of the document inside the docs folder.</summary>
    public string Path { get; }
    public string Title { get; }
    /// <summary>The document was deleted or moved outside the app.</summary>
    public bool IsMissing { get; }
    public string Detail => IsMissing ? Path + "  (not found)" : Path;
}

/// <summary>An entry of a list to pick from (tasks, documents).</summary>
public class PickItem
{
    public PickItem(string id, string title, string detail = "")
    {
        Id = id;
        Title = title;
        Detail = detail;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>Dialog that picks one item of a list, with a filter box.</summary>
public class ItemPickerViewModel : DialogViewModel
{
    private readonly List<PickItem> _all;

    public ItemPickerViewModel(string title, IEnumerable<PickItem> items, string okText = "Link", string emptyText = "Nothing to choose from.")
    {
        Title = title;
        OkText = okText;
        EmptyText = emptyText;
        _all = items.ToList();
        OkCommand = new RelayCommand(() => { if (Selected != null) Close(true); });
        CancelCommand = new RelayCommand(() => Close(false));
        Apply();
    }

    public string Title { get; }
    public string OkText { get; }
    public string EmptyText { get; }
    public bool IsEmpty => _all.Count == 0;
    public ObservableCollection<PickItem> Items { get; } = new();

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) Apply(); }
    }

    private PickItem? _selected;
    public PickItem? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }

    private void Apply()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Items.Clear();
        foreach (var item in _all)
            if (words.All(w => item.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                               item.Detail.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                Items.Add(item);
        Selected = Items.FirstOrDefault();
    }
}

public class TagOption : ObservableObject
{
    public TagOption(TagDef tag, bool isSelected)
    {
        Tag = tag;
        _isSelected = isSelected;
    }

    public TagDef Tag { get; }
    public string Name => Tag.Name;
    public string Color => Tag.Color;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public class ChecklistItemViewModel : ObservableObject
{
    public ChecklistItemViewModel(ChecklistItem item, Action changed)
    {
        Id = item.Id;
        _text = item.Text;
        _done = item.Done;
        _changed = changed;
    }

    private readonly Action _changed;
    public string Id { get; }

    private string _text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    private bool _done;
    public bool Done
    {
        get => _done;
        set { if (SetProperty(ref _done, value)) _changed(); }
    }

    public ChecklistItem ToModel() => new() { Id = Id, Text = (Text ?? "").Trim(), Done = Done };
}

public class CommentViewModel
{
    public CommentViewModel(Comment comment, BoardViewModel board, bool canDelete)
    {
        Comment = comment;
        var author = board.FindMember(comment.AuthorId);
        AuthorName = author?.Username ?? comment.AuthorName;
        Initials = Account.MakeInitials(AuthorName);
        Color = author?.AvatarColor ?? "#8D8D8D";
        Image = author?.AvatarImage;
        When = comment.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        CanDelete = canDelete;
    }

    public Comment Comment { get; }
    public string AuthorName { get; }
    public string Initials { get; }
    public string Color { get; }
    public string? Image { get; }
    public string When { get; }
    public string Text => Comment.Text;
    public bool CanDelete { get; }
}

public class ActivityViewModel
{
    public ActivityViewModel(ActivityEntry entry, BoardViewModel board)
    {
        var name = board.FindMember(entry.UserId)?.Username ?? entry.UserName;
        Text = $"{name} {entry.Text}";
        When = entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public string Text { get; }
    public string When { get; }
}

/// <summary>
/// Card dialog (create / edit). It works on a draft: nothing touches the board until Save,
/// when the differences are also written to the card's activity log.
/// </summary>
public class CardEditorViewModel : DialogViewModel
{
    private readonly BoardViewModel _board;
    private readonly IDialogService _dialogs;
    private readonly CardData _original;
    private readonly string _baselineJson;
    private readonly List<Comment> _comments;
    private readonly List<ActivityEntry> _pendingActivity = new();

    public CardEditorViewModel(BoardViewModel board, CardData original, ColumnViewModel column, bool isNew)
    {
        _board = board;
        _dialogs = board.Dialogs;
        _original = original;
        IsNew = isNew;

        _title = original.Title;
        _description = original.Description;
        _priority = original.Priority;
        _dueDate = original.DueDate?.Date;
        _dueTime = original.DueDate != null && original.DueHasTime
            ? original.DueDate.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        _endDate = original.EndDate?.Date;
        _endTime = original.EndDate != null && original.EndHasTime
            ? original.EndDate.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        _startDate = original.StartDate?.Date;
        _finishDate = original.FinishDate?.Date;
        foreach (var path in original.DocRefs ?? new List<string>()) References.Add(board.ReferenceRow(path));
        _comments = original.Comments.Select(c => JsonFile.Clone(c)).ToList();

        AssigneeOptions = new List<AssigneeOption> { new(null, "Unassigned", "#C5CCD3") };
        AssigneeOptions.AddRange(board.Members.Select(m => new AssigneeOption(m.Id, m.Username, m.AvatarColor, m.AvatarImage)));
        if (original.AssigneeId != null && AssigneeOptions.All(o => o.Id != original.AssigneeId))
            AssigneeOptions.Add(new AssigneeOption(original.AssigneeId, "(removed user)", "#8D8D8D"));
        _selectedAssignee = AssigneeOptions.First(o => o.Id == original.AssigneeId);

        Columns = board.Columns.ToList();
        _selectedColumn = column;

        // Dependencies: parent task and subtasks.
        ParentOptions = new List<CardOption> { new(null, "None (top-level task)") };
        foreach (var boardColumn in board.Data.Columns)
            foreach (var candidate in boardColumn.Cards)
                if (board.Index.CanBeChildOf(original.Id, candidate.Id))
                    ParentOptions.Add(new CardOption(candidate.Id, $"{candidate.Title}  ·  {boardColumn.Name}"));
        _selectedParent = ParentOptions.FirstOrDefault(o => o.Id == original.ParentId) ?? ParentOptions[0];
        foreach (var child in board.Index.ChildrenOf(original.Id)) Subtasks.Add(RowFor(child));
        RebuildSubtaskCandidates();

        foreach (var item in original.Checklist)
            Checklist.Add(new ChecklistItemViewModel(item, OnChecklistChanged));
        RebuildTagOptions(original.TagIds);
        RebuildComments();
        Activity = original.Activity.AsEnumerable().Reverse().Select(a => new ActivityViewModel(a, board)).ToList();

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        ClearDueCommand = new RelayCommand(() => { DueDate = null; DueTime = ""; });
        ClearEndCommand = new RelayCommand(() => { EndDate = null; EndTime = ""; });
        ClearStartCommand = new RelayCommand(() => StartDate = null);
        ClearFinishCommand = new RelayCommand(() => FinishDate = null);
        AddReferenceCommand = new RelayCommand(AddReference);
        RemoveReferenceCommand = new RelayCommand<ReferenceRow>(row =>
        {
            if (row is null || !References.Remove(row)) return;
            OnPropertyChanged(nameof(ReferencesHeader));
            OnPropertyChanged(nameof(HasReferences));
        });
        AddChecklistItemCommand = new RelayCommand(AddChecklistItem);
        RemoveChecklistItemCommand = new RelayCommand<ChecklistItemViewModel>(item =>
        {
            if (item != null && Checklist.Remove(item)) OnChecklistChanged();
        });
        PostCommentCommand = new RelayCommand(PostComment);
        DeleteCommentCommand = new RelayCommand<CommentViewModel>(DeleteComment);
        ManageTagsCommand = new RelayCommand(ManageTags);
        TogglePreviewCommand = new RelayCommand(() => ShowPreview = !ShowPreview);
        AddExistingSubtaskCommand = new RelayCommand(AddExistingSubtask);
        AddNewSubtaskCommand = new RelayCommand(AddNewSubtask);
        RemoveSubtaskCommand = new RelayCommand<SubtaskRow>(row =>
        {
            if (row is null || !Subtasks.Remove(row)) return;
            RebuildSubtaskCandidates();
            OnPropertyChanged(nameof(SubtasksHeader));
        });

        _baselineJson = StateJson();
    }

    public bool IsNew { get; }
    public string WindowTitle => IsNew ? "New card" : "Edit card";

    /// <summary>The card to store, available after the dialog closes with Save.</summary>
    public CardData? Result { get; private set; }

    public string MetaText => IsNew
        ? $"Will be created by {_board.CurrentUser.Username}"
        : $"Created by {_board.FindMember(_original.CreatorId)?.Username ?? _original.CreatorName} on " +
          $"{_original.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  ·  Last modified {_original.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    // ------------------------------------------------------------------ fields

    private string _title;
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _description;
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private bool _showPreview;
    /// <summary>Toggles between the markdown editor and its rendered preview.</summary>
    public bool ShowPreview
    {
        get => _showPreview;
        set
        {
            if (!SetProperty(ref _showPreview, value)) return;
            OnPropertyChanged(nameof(ShowEditor));
            OnPropertyChanged(nameof(PreviewButtonText));
        }
    }

    public bool ShowEditor => !_showPreview;
    public string PreviewButtonText => _showPreview ? "Edit" : "Preview";

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();

    private Priority _priority;
    public Priority Priority
    {
        get => _priority;
        set { if (SetProperty(ref _priority, value)) OnPropertyChanged(nameof(PriorityColor)); }
    }

    public string PriorityColor => Palette.PriorityColor(_priority);

    public List<AssigneeOption> AssigneeOptions { get; }

    private AssigneeOption _selectedAssignee;
    public AssigneeOption SelectedAssignee
    {
        get => _selectedAssignee;
        set => SetProperty(ref _selectedAssignee, value ?? AssigneeOptions[0]);
    }

    public IReadOnlyList<ColumnViewModel> Columns { get; }

    private ColumnViewModel? _selectedColumn;
    public ColumnViewModel? SelectedColumn { get => _selectedColumn; set => SetProperty(ref _selectedColumn, value); }

    // ------------------------------------------------------------------ dependencies

    /// <summary>Cards this one can be a subtask of (first entry = none).</summary>
    public List<CardOption> ParentOptions { get; }

    private CardOption _selectedParent;
    public CardOption SelectedParent
    {
        get => _selectedParent;
        set => SetProperty(ref _selectedParent, value ?? ParentOptions[0]);
    }

    public ObservableCollection<SubtaskRow> Subtasks { get; } = new();
    public string SubtasksHeader => Subtasks.Count == 0 ? "Subtasks" : $"Subtasks ({Subtasks.Count})";

    /// <summary>Existing cards that can still be added as subtasks.</summary>
    public ObservableCollection<CardOption> SubtaskCandidates { get; } = new();
    public bool HasSubtaskCandidates => SubtaskCandidates.Count > 0;

    private CardOption? _selectedCandidate;
    public CardOption? SelectedCandidate { get => _selectedCandidate; set => SetProperty(ref _selectedCandidate, value); }

    private string _newSubtaskTitle = "";
    public string NewSubtaskTitle { get => _newSubtaskTitle; set => SetProperty(ref _newSubtaskTitle, value); }

    public ICommand AddExistingSubtaskCommand { get; }
    public ICommand AddNewSubtaskCommand { get; }
    public ICommand RemoveSubtaskCommand { get; }

    /// <summary>After Save: the existing cards that must be subtasks of this card.</summary>
    public IReadOnlyCollection<string> ResultSubtaskIds { get; private set; } = Array.Empty<string>();

    /// <summary>After Save: titles of the subtasks to create.</summary>
    public IReadOnlyList<string> NewSubtaskTitles { get; private set; } = Array.Empty<string>();

    private SubtaskRow RowFor(CardData card)
    {
        var column = _board.Index.ColumnOf(card.Id)?.Name ?? "";
        var progress = _board.Index.Progress(card);
        return new SubtaskRow(card.Id, card.Title, $"{column}  ·  {Math.Round(progress * 100)}%", progress);
    }

    private void RebuildSubtaskCandidates()
    {
        SubtaskCandidates.Clear();
        foreach (var column in _board.Data.Columns)
        {
            foreach (var card in column.Cards)
            {
                if (card.Id == _original.Id || Subtasks.Any(r => r.Id == card.Id)) continue;
                if (!_board.Index.CanBeChildOf(card.Id, _original.Id)) continue;
                SubtaskCandidates.Add(new CardOption(card.Id, $"{card.Title}  ·  {column.Name}"));
            }
        }
        SelectedCandidate = SubtaskCandidates.FirstOrDefault();
        OnPropertyChanged(nameof(HasSubtaskCandidates));
    }

    private void AddExistingSubtask()
    {
        var card = _board.Index.Find(SelectedCandidate?.Id);
        if (card is null) return;
        Subtasks.Add(RowFor(card));
        RebuildSubtaskCandidates();
        OnPropertyChanged(nameof(SubtasksHeader));
    }

    private void AddNewSubtask()
    {
        var title = (NewSubtaskTitle ?? "").Trim();
        if (title.Length == 0) return;
        Subtasks.Add(new SubtaskRow(null, title, "new card", 0));
        NewSubtaskTitle = "";
        OnPropertyChanged(nameof(SubtasksHeader));
    }

    /// <summary>Everything that Save would store, as one string: used to detect unsaved changes.</summary>
    private string StateJson() =>
        JsonFile.Serialize(BuildDraft()) + "|" + string.Join(",", Subtasks.Select(r => r.Id ?? "new:" + r.Title));

    public ObservableCollection<TagOption> TagOptions { get; } = new();
    public bool HasTagOptions => TagOptions.Count > 0;

    private DateTime? _dueDate;
    public DateTime? DueDate
    {
        get => _dueDate;
        set { if (SetProperty(ref _dueDate, value)) OnPropertyChanged(nameof(HasDueDate)); }
    }

    public bool HasDueDate => _dueDate != null;

    private string _dueTime;
    /// <summary>Optional time of the due date, as "HH:mm".</summary>
    public string DueTime { get => _dueTime; set => SetProperty(ref _dueTime, value ?? ""); }

    private DateTime? _endDate;
    /// <summary>When the task was completed.</summary>
    public DateTime? EndDate
    {
        get => _endDate;
        set { if (SetProperty(ref _endDate, value)) OnPropertyChanged(nameof(HasEndDate)); }
    }

    public bool HasEndDate => _endDate != null;

    private string _endTime;
    /// <summary>Optional time of the end date, as "HH:mm".</summary>
    public string EndTime { get => _endTime; set => SetProperty(ref _endTime, value ?? ""); }

    private DateTime? _startDate;
    /// <summary>Roadmap: the day the work started.</summary>
    public DateTime? StartDate
    {
        get => _startDate;
        set { if (SetProperty(ref _startDate, value)) OnPropertyChanged(nameof(HasStartDate)); }
    }

    public bool HasStartDate => _startDate != null;

    private DateTime? _finishDate;
    /// <summary>Roadmap: replaces due and end date there (one plain bar from start to finish).</summary>
    public DateTime? FinishDate
    {
        get => _finishDate;
        set { if (SetProperty(ref _finishDate, value)) OnPropertyChanged(nameof(HasFinishDate)); }
    }

    public bool HasFinishDate => _finishDate != null;

    public string RoadmapHint =>
        "Optional, used only by the Roadmap: a task appears there when it has a start date plus an end or a finish date.";

    // ------------------------------------------------------------------ references (documents)

    public ObservableCollection<ReferenceRow> References { get; } = new();
    public bool HasReferences => References.Count > 0;
    public string ReferencesHeader => References.Count == 0 ? "References" : $"References ({References.Count})";

    public ICommand AddReferenceCommand { get; }
    public ICommand RemoveReferenceCommand { get; }

    private void AddReference()
    {
        var items = _board.DocumentChoices()
            .Where(i => References.All(r => !r.Path.Equals(i.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        var picker = new ItemPickerViewModel("Link a document", items, "Link",
            "There are no documents to link yet: create them in the Documentation area.");
        if (_dialogs.ShowDialog(picker) != true || picker.Selected is null) return;
        References.Add(_board.ReferenceRow(picker.Selected.Id));
        OnPropertyChanged(nameof(ReferencesHeader));
        OnPropertyChanged(nameof(HasReferences));
    }

    // ------------------------------------------------------------------ checklist

    public ObservableCollection<ChecklistItemViewModel> Checklist { get; } = new();

    private string _newChecklistText = "";
    public string NewChecklistText { get => _newChecklistText; set => SetProperty(ref _newChecklistText, value); }

    public string ChecklistProgress =>
        Checklist.Count == 0 ? "" : $"{Checklist.Count(i => i.Done)}/{Checklist.Count} done";

    private void OnChecklistChanged() => OnPropertyChanged(nameof(ChecklistProgress));

    private void AddChecklistItem()
    {
        var text = (NewChecklistText ?? "").Trim();
        if (text.Length == 0) return;
        Checklist.Add(new ChecklistItemViewModel(new ChecklistItem { Text = text }, OnChecklistChanged));
        NewChecklistText = "";
        OnChecklistChanged();
    }

    // ------------------------------------------------------------------ comments

    public ObservableCollection<CommentViewModel> Comments { get; } = new();
    public string CommentsHeader => _comments.Count == 0 ? "Comments" : $"Comments ({_comments.Count})";

    private string _newCommentText = "";
    public string NewCommentText { get => _newCommentText; set => SetProperty(ref _newCommentText, value); }

    public string CurrentUserInitials => _board.CurrentUser.Initials;
    public string CurrentUserColor => _board.CurrentUser.AvatarColor;
    public string? CurrentUserImage => _board.CurrentUser.AvatarImage;

    private void RebuildComments()
    {
        Comments.Clear();
        var me = _board.CurrentUser.Id;
        foreach (var comment in _comments)
            Comments.Add(new CommentViewModel(comment, _board,
                canDelete: comment.AuthorId == me || _original.CreatorId == me));
        OnPropertyChanged(nameof(CommentsHeader));
    }

    private ActivityEntry NewActivity(string text) => new()
    {
        At = DateTime.UtcNow,
        UserId = _board.CurrentUser.Id,
        UserName = _board.CurrentUser.Username,
        Text = text
    };

    private void PostComment()
    {
        var text = (NewCommentText ?? "").Trim();
        if (text.Length == 0) return;
        _comments.Add(new Comment
        {
            AuthorId = _board.CurrentUser.Id,
            AuthorName = _board.CurrentUser.Username,
            Text = text
        });
        _pendingActivity.Add(NewActivity("added a comment"));
        NewCommentText = "";
        RebuildComments();
    }

    private void DeleteComment(CommentViewModel? comment)
    {
        if (comment is null || !comment.CanDelete) return;
        if (!_dialogs.Confirm("Delete this comment?", "Delete comment")) return;
        _comments.Remove(comment.Comment);
        _pendingActivity.Add(NewActivity($"deleted a comment by {comment.AuthorName}"));
        RebuildComments();
    }

    // ------------------------------------------------------------------ activity

    public IReadOnlyList<ActivityViewModel> Activity { get; }
    public bool HasActivity => Activity.Count > 0;

    // ------------------------------------------------------------------ tags

    private void RebuildTagOptions(IEnumerable<string> selectedIds)
    {
        var selected = selectedIds.ToHashSet();
        TagOptions.Clear();
        foreach (var tag in _board.Data.Tags) TagOptions.Add(new TagOption(tag, selected.Contains(tag.Id)));
        OnPropertyChanged(nameof(HasTagOptions));
    }

    private void ManageTags()
    {
        var selected = TagOptions.Where(o => o.IsSelected).Select(o => o.Tag.Id).ToList();
        _board.ManageTags();
        RebuildTagOptions(selected);
    }

    // ------------------------------------------------------------------ commands

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearDueCommand { get; }
    public ICommand ClearEndCommand { get; }
    public ICommand ClearStartCommand { get; }
    public ICommand ClearFinishCommand { get; }
    public ICommand AddChecklistItemCommand { get; }
    public ICommand RemoveChecklistItemCommand { get; }
    public ICommand PostCommentCommand { get; }
    public ICommand DeleteCommentCommand { get; }
    public ICommand ManageTagsCommand { get; }
    public ICommand TogglePreviewCommand { get; }

    // ------------------------------------------------------------------ draft / save

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        text = (text ?? "").Trim().Replace('.', ':');
        if (text.Length == 0) return false;
        if (!text.Contains(':')) text += ":00";
        if (!DateTime.TryParseExact(text, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return false;
        time = parsed.TimeOfDay;
        return true;
    }

    /// <summary>Builds a card from the current content of the dialog (activity log excluded).</summary>
    private CardData BuildDraft()
    {
        var draft = new CardData
        {
            Id = _original.Id,
            Title = (Title ?? "").Trim(),
            Description = (Description ?? "").Trim(),
            Priority = Priority,
            AssigneeId = SelectedAssignee.Id,
            ParentId = SelectedParent.Id,
            TagIds = TagOptions.Where(o => o.IsSelected).Select(o => o.Tag.Id).ToList(),
            Checklist = Checklist.Select(i => i.ToModel()).Where(i => i.Text.Length > 0).ToList(),
            CreatorId = _original.CreatorId,
            CreatorName = _original.CreatorName,
            CreatedAt = _original.CreatedAt,
            UpdatedAt = _original.UpdatedAt,
            Comments = _comments.ToList(),
            Activity = _original.Activity.ToList(),
            ArchivedFromColumnId = _original.ArchivedFromColumnId,
            ArchivedAt = _original.ArchivedAt
        };

        if (DueDate != null)
        {
            var date = DateTime.SpecifyKind(DueDate.Value.Date, DateTimeKind.Unspecified);
            if (TryParseTime(DueTime, out var time))
            {
                draft.DueDate = date + time;
                draft.DueHasTime = true;
            }
            else
            {
                draft.DueDate = date;
            }
        }

        if (EndDate != null)
        {
            var date = DateTime.SpecifyKind(EndDate.Value.Date, DateTimeKind.Unspecified);
            if (TryParseTime(EndTime, out var time))
            {
                draft.EndDate = date + time;
                draft.EndHasTime = true;
            }
            else
            {
                draft.EndDate = date;
            }
        }
        if (StartDate != null) draft.StartDate = DateTime.SpecifyKind(StartDate.Value.Date, DateTimeKind.Unspecified);
        if (FinishDate != null) draft.FinishDate = DateTime.SpecifyKind(FinishDate.Value.Date, DateTimeKind.Unspecified);
        if (References.Count > 0) draft.DocRefs = References.Select(r => r.Path).ToList();
        return draft;
    }

    /// <summary>True when the dialog content differs from what was loaded.</summary>
    public bool IsDirty =>
        StateJson() != _baselineJson || !string.IsNullOrWhiteSpace(NewCommentText);

    /// <summary>Asked by the window before closing without saving.</summary>
    public bool ConfirmDiscard() =>
        !IsDirty || _dialogs.Confirm("Discard the changes made to this card?", "Unsaved changes");

    private void Save()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Error = "The title is required.";
            return;
        }
        if (DueDate != null && !string.IsNullOrWhiteSpace(DueTime) && !TryParseTime(DueTime, out _))
        {
            Error = "The due time must be in the HH:mm format (for example 18:30).";
            return;
        }
        if (EndDate != null && !string.IsNullOrWhiteSpace(EndTime) && !TryParseTime(EndTime, out _))
        {
            Error = "The end time must be in the HH:mm format (for example 18:30).";
            return;
        }
        if (StartDate != null && FinishDate != null && FinishDate.Value.Date < StartDate.Value.Date)
        {
            Error = "The finish date cannot be before the start date.";
            return;
        }
        if (StartDate != null && EndDate != null && EndDate.Value.Date < StartDate.Value.Date)
        {
            Error = "The end date cannot be before the start date.";
            return;
        }
        // Dependencies must not form a loop: parent -> this card -> subtask -> ... -> parent.
        var parentId = SelectedParent.Id;
        if (parentId != null && Subtasks.Any(r => r.Id != null &&
                (r.Id == parentId || _board.Index.IsDescendant(parentId, r.Id))))
        {
            Error = "A card cannot be the parent task and, at the same time, one of the subtasks.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(NewSubtaskTitle)) AddNewSubtask();        // do not lose a typed subtask
        if (!string.IsNullOrWhiteSpace(NewCommentText)) PostComment(); // do not lose a typed comment

        var draft = BuildDraft();
        var changes = IsNew ? new List<string> { "created this card" } : DescribeChanges(_original, draft);

        foreach (var text in changes) draft.Activity.Add(NewActivity(text));
        draft.Activity.AddRange(_pendingActivity);
        if (changes.Count > 0 || _pendingActivity.Count > 0) draft.UpdatedAt = DateTime.UtcNow;

        ResultSubtaskIds = Subtasks.Where(r => r.Id != null).Select(r => r.Id!).ToList();
        NewSubtaskTitles = Subtasks.Where(r => r.IsNew).Select(r => r.Title).ToList();
        Result = draft;
        Close(true);
    }

    /// <summary>Human-readable list of what changed, for the activity log.</summary>
    private List<string> DescribeChanges(CardData before, CardData after)
    {
        var changes = new List<string>();

        if (before.Title != after.Title)
            changes.Add($"renamed this card from \"{before.Title}\" to \"{after.Title}\"");
        if ((before.Description ?? "") != after.Description)
            changes.Add(after.Description.Length == 0 ? "removed the description" : "updated the description");
        if (before.Priority != after.Priority)
            changes.Add($"changed priority from {before.Priority} to {after.Priority}");
        if (before.AssigneeId != after.AssigneeId)
            changes.Add(after.AssigneeId is null
                ? "removed the assignee"
                : $"assigned this card to {_board.MemberName(after.AssigneeId)}");

        if (before.ParentId != after.ParentId)
            changes.Add(after.ParentId is null
                ? "is no longer a subtask"
                : $"became a subtask of \"{_board.Index.Find(after.ParentId)?.Title}\"");

        foreach (var id in after.TagIds.Except(before.TagIds))
            changes.Add($"added the tag {_board.FindTag(id)?.Name}");
        foreach (var id in before.TagIds.Except(after.TagIds))
            if (_board.FindTag(id) is { } tag) changes.Add($"removed the tag {tag.Name}");

        if (before.DueDate != after.DueDate || before.DueHasTime != after.DueHasTime)
            changes.Add(after.DueDate is null
                ? "removed the due date"
                : $"set the due date to {CardViewModel.FormatDue(after.DueDate, after.DueHasTime)}");

        if (before.EndDate != after.EndDate || before.EndHasTime != after.EndHasTime)
            changes.Add(after.EndDate is null
                ? "removed the end date"
                : $"set the end date to {CardViewModel.FormatDue(after.EndDate, after.EndHasTime)}");
        if (before.StartDate != after.StartDate)
            changes.Add(after.StartDate is null
                ? "removed the start date"
                : $"set the start date to {CardViewModel.FormatDue(after.StartDate, false)}");
        if (before.FinishDate != after.FinishDate)
            changes.Add(after.FinishDate is null
                ? "removed the finish date"
                : $"set the finish date to {CardViewModel.FormatDue(after.FinishDate, false)}");

        var oldRefs = before.DocRefs ?? new List<string>();
        var newRefs = after.DocRefs ?? new List<string>();
        foreach (var path in newRefs.Except(oldRefs, StringComparer.OrdinalIgnoreCase))
            changes.Add($"linked the document {DocsService.TitleOf(DocsService.FileNameOf(path))}");
        foreach (var path in oldRefs.Except(newRefs, StringComparer.OrdinalIgnoreCase))
            changes.Add($"removed the link to the document {DocsService.TitleOf(DocsService.FileNameOf(path))}");

        var oldList = string.Join("\n", before.Checklist.Select(i => $"{i.Done}|{i.Text}"));
        var newList = string.Join("\n", after.Checklist.Select(i => $"{i.Done}|{i.Text}"));
        if (oldList != newList)
            changes.Add(after.Checklist.Count == 0
                ? "removed the checklist"
                : $"updated the checklist ({after.Checklist.Count(i => i.Done)}/{after.Checklist.Count} done)");

        return changes;
    }
}
