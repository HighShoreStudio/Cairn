using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HighshoreCairn.Models;

namespace HighshoreCairn.ViewModels;

/// <summary>An entry of a card context sub-menu (Priority / Assign to / Tags / Move to).</summary>
public class ChoiceItem
{
    public ChoiceItem(string label, bool isChecked, ICommand command, string color = "#00FFFFFF")
    {
        Label = MenuText.Escape(label);
        IsChecked = isChecked;
        Command = command;
        Color = color;
    }

    public string Label { get; }
    public bool IsChecked { get; }
    public ICommand Command { get; }
    public string Color { get; }
}

/// <summary>A reference shown on a card: the whiteboard element or a document linked to the task.</summary>
public class CardReference
{
    public CardReference(string kind, string label, string toolTip, bool isMissing, ICommand openCommand)
    {
        Kind = kind;
        Label = label;
        ToolTip = toolTip;
        IsMissing = isMissing;
        OpenCommand = openCommand;
    }

    /// <summary>"whiteboard" or "doc": picks the small icon.</summary>
    public string Kind { get; }
    public bool IsWhiteboard => Kind == "whiteboard";
    public bool IsDocument => Kind == "doc";
    public string Label { get; }
    public string ToolTip { get; }
    public bool IsMissing { get; }
    public ICommand OpenCommand { get; }
}

/// <summary>A card as displayed on the board. Wraps a <see cref="CardData"/>.</summary>
public class CardViewModel : ObservableObject
{
    private readonly BoardViewModel _board;

    public CardViewModel(BoardViewModel board, CardData data, ColumnViewModel column)
    {
        _board = board;
        Data = data;
        Column = column;
        EditCommand = new RelayCommand(() => _board.EditCard(this));
        DeleteCommand = new RelayCommand(() => _board.DeleteCard(this));
        ArchiveCommand = new RelayCommand(() => _board.ArchiveCard(this));
    }

    public CardData Data { get; }
    public ColumnViewModel Column { get; set; }

    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ArchiveCommand { get; }

    // ------------------------------------------------------------------ display

    public string Title => Data.Title;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Data.Description);

    /// <summary>Short plain-text version of the description (markdown symbols removed).</summary>
    public string DescriptionPreview
    {
        get
        {
            if (!HasDescription) return "";
            var text = Data.Description.Replace("\r", " ").Replace("\n", " ")
                .Replace("**", "").Replace("`", "").Replace("#", "").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length > 160 ? text[..160] + "…" : text;
        }
    }

    public Priority Priority => Data.Priority;
    public bool HasPriority => Data.Priority != Priority.None;
    public string PriorityLabel => Data.Priority.ToString();
    public string PriorityColor => Palette.PriorityColor(Data.Priority);

    public IReadOnlyList<TagDef> Tags => Data.TagIds
        .Select(id => _board.FindTag(id))
        .Where(t => t != null)
        .Select(t => t!)
        .ToList();

    public bool HasTags => Tags.Count > 0;

    public bool HasAssignee => Data.AssigneeId != null;
    public string AssigneeName => _board.MemberName(Data.AssigneeId) ?? "Unassigned";
    public string AssigneeInitials => Account.MakeInitials(_board.MemberName(Data.AssigneeId));
    public string AssigneeColor => _board.FindMember(Data.AssigneeId)?.AvatarColor ?? "#8D8D8D";
    public string? AssigneeImage => _board.FindMember(Data.AssigneeId)?.AvatarImage;

    public bool HasDue => Data.DueDate != null;
    public string DueText => FormatDue(Data.DueDate, Data.DueHasTime);
    public bool IsOverdue => FilterViewModel.IsOverdue(Data, Column.IsDone, DateTime.Now);
    public bool IsDueSoon => !IsOverdue && !Column.IsDone && Data.DueMoment is { } m && m < DateTime.Now.AddDays(2);

    public bool HasChecklist => Data.Checklist.Count > 0;
    public string ChecklistText => $"{Data.Checklist.Count(i => i.Done)}/{Data.Checklist.Count}";
    public bool ChecklistComplete => HasChecklist && Data.Checklist.All(i => i.Done);

    public bool HasComments => Data.Comments.Count > 0;
    public string CommentCount => Data.Comments.Count.ToString();

    // ------------------------------------------------------------------ completion & dependencies

    /// <summary>0..1, from the checklist and the subtasks (1 when the card is in a "done" column).</summary>
    public double Progress => _board.Index.Progress(Data);

    /// <summary>The card sits in a "done" column: it is drawn dimmed.</summary>
    public bool IsCompleted => Column.IsDone;

    public string ProgressToolTip
    {
        get
        {
            var text = $"{Math.Round(Progress * 100)}% complete";
            if (IsCompleted) return text + " (in a done column)";
            if (HasChecklist) text += $"\nChecklist: {ChecklistText}";
            if (HasSubtasks) text += $"\nSubtasks: {SubtaskText}";
            if (!HasChecklist && !HasSubtasks) text += "\nAdd checklist items or subtasks to track progress.";
            return text;
        }
    }

    public bool HasSubtasks => _board.Index.ChildrenOf(Data.Id).Count > 0;

    public string SubtaskText
    {
        get
        {
            var (done, total) = _board.Index.SubtaskCount(Data.Id);
            return $"{done}/{total}";
        }
    }

    // ------------------------------------------------------------------ references

    /// <summary>
    /// What the task is linked to: the whiteboard element that references it (always first), then the
    /// documents chosen in the card dialog. Each entry opens its target.
    /// </summary>
    public IReadOnlyList<CardReference> References
    {
        get
        {
            var list = new List<CardReference>();
            if (_board.WhiteboardLinkOf(Data.Id) is { } link)
            {
                list.Add(new CardReference("whiteboard", link.Label,
                    $"Whiteboard '{link.BoardName}': click to show the element", false,
                    new RelayCommand(() => _board.GoToWhiteboardElement(link.BoardId, link.ElementId))));
            }
            foreach (var path in Data.DocRefs ?? new List<string>())
            {
                var row = _board.ReferenceRow(path);
                var target = path;
                list.Add(new CardReference("doc", row.Title,
                    row.IsMissing ? $"{path}\nThe document no longer exists." : $"{path}\nClick to open the document", row.IsMissing,
                    new RelayCommand(() => _board.GoToDocument(target))));
            }
            return list;
        }
    }

    public bool HasReferences => _board.WhiteboardLinkOf(Data.Id) != null || Data.DocRefs is { Count: > 0 };

    private bool _isHighlighted;
    /// <summary>Marked on the board after "go to task" (from the whiteboard graph): selected, not opened.</summary>
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }

    public bool HasParent => _board.Index.ParentOf(Data) != null;
    public string ParentTitle => _board.Index.ParentOf(Data)?.Title ?? "";

    public string ToolTip =>
        $"Created by {Data.CreatorName} on {Data.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
        $"Last modified {Data.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public static string FormatDue(DateTime? due, bool hasTime)
    {
        if (due is null) return "";
        var d = due.Value;
        var text = d.Year == DateTime.Now.Year
            ? d.ToString("MMM d", CultureInfo.InvariantCulture)
            : d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return hasTime ? text + " " + d.ToString("HH:mm", CultureInfo.InvariantCulture) : text;
    }

    // ------------------------------------------------------------------ drag & drop feedback

    private bool _dropAbove;
    public bool DropAbove { get => _dropAbove; set => SetProperty(ref _dropAbove, value); }

    private bool _dropBelow;
    public bool DropBelow { get => _dropBelow; set => SetProperty(ref _dropBelow, value); }

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; set => SetProperty(ref _isDragging, value); }

    // ------------------------------------------------------------------ inline edit (context menu)

    public IReadOnlyList<ChoiceItem> PriorityChoices =>
        Enum.GetValues<Priority>()
            .Select(p => new ChoiceItem(p.ToString(), Data.Priority == p,
                new RelayCommand(() => _board.SetPriority(this, p)), Palette.PriorityColor(p)))
            .ToList();

    public IReadOnlyList<ChoiceItem> AssigneeChoices
    {
        get
        {
            var list = new List<ChoiceItem>
            {
                new("Unassigned", Data.AssigneeId is null, new RelayCommand(() => _board.SetAssignee(this, null)))
            };
            list.AddRange(_board.Members.Select(m => new ChoiceItem(m.Username, Data.AssigneeId == m.Id,
                new RelayCommand(() => _board.SetAssignee(this, m.Id)), m.AvatarColor)));
            return list;
        }
    }

    public IReadOnlyList<ChoiceItem> TagChoices =>
        _board.Data.Tags
            .Select(t => new ChoiceItem(t.Name, Data.TagIds.Contains(t.Id),
                new RelayCommand(() => _board.ToggleTag(this, t.Id)), t.Color))
            .ToList();

    /// <summary>"Subtask of" sub-menu: every card that can be the parent without creating a loop.</summary>
    public IReadOnlyList<ChoiceItem> ParentChoices
    {
        get
        {
            var list = new List<ChoiceItem>
            {
                new("None (top-level task)", !HasParent, new RelayCommand(() => _board.SetParent(this, null)))
            };
            foreach (var column in _board.Data.Columns)
            {
                foreach (var candidate in column.Cards)
                {
                    if (!_board.Index.CanBeChildOf(Data.Id, candidate.Id)) continue;
                    var id = candidate.Id;
                    list.Add(new ChoiceItem($"{candidate.Title}  ·  {column.Name}", Data.ParentId == id,
                        new RelayCommand(() => _board.SetParent(this, id))));
                }
            }
            return list;
        }
    }

    public IReadOnlyList<ChoiceItem> MoveChoices =>
        _board.Columns
            .Select(c => new ChoiceItem(c.Name, c == Column,
                new RelayCommand(() => _board.MoveCardToColumn(this, c))))
            .ToList();
}

/// <summary>A column of the board. Wraps a <see cref="ColumnData"/>.</summary>
public class ColumnViewModel : ObservableObject
{
    private readonly BoardViewModel _board;

    public ColumnViewModel(BoardViewModel board, ColumnData data)
    {
        _board = board;
        Data = data;
        AddCardCommand = new RelayCommand(() => _board.AddCard(this));
        RenameCommand = new RelayCommand(() => _board.RenameColumn(this));
        DeleteCommand = new RelayCommand(() => _board.DeleteColumn(this));
        MoveLeftCommand = new RelayCommand(() => _board.MoveColumn(this, -1));
        MoveRightCommand = new RelayCommand(() => _board.MoveColumn(this, +1));
        EditStyleCommand = new RelayCommand(() => _board.EditColumnStyle(this));
    }

    public ICommand EditStyleCommand { get; }

    /// <summary>Custom background (hex) for the current theme, or null for the theme default.</summary>
    public string? BackgroundColor => _board.IsDark ? Data.BackgroundDark : Data.Background;
    public bool HasCustomBackground => Palette.IsValidHex(BackgroundColor);

    /// <summary>Custom color of the column name (hex) for the current theme, or null for the theme default.</summary>
    public string? TitleColor => _board.IsDark ? Data.TitleColorDark : Data.TitleColor;
    public bool HasCustomTitleColor => Palette.IsValidHex(TitleColor);

    public ColumnData Data { get; }
    public string Name => Data.Name;

    /// <summary>The cards currently shown (after filters and sort).</summary>
    public ObservableCollection<CardViewModel> VisibleCards { get; } = new();

    public ICommand AddCardCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveLeftCommand { get; }
    public ICommand MoveRightCommand { get; }

    /// <summary>Cards of a "done" column are never overdue (toggle in the column menu).</summary>
    public bool IsDone
    {
        get => Data.IsDone;
        set
        {
            if (Data.IsDone == value) return;
            Data.IsDone = value;
            OnPropertyChanged();
            _board.OnColumnChanged();
        }
    }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }

    private bool _isDropTarget;
    public bool IsDropTarget { get => _isDropTarget; set => SetProperty(ref _isDropTarget, value); }

    /// <summary>"5" normally, "2 / 5" while a filter hides some cards.</summary>
    public string CountText =>
        VisibleCards.Count == Data.Cards.Count ? Data.Cards.Count.ToString() : $"{VisibleCards.Count} / {Data.Cards.Count}";

    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(HasCustomBackground));
        OnPropertyChanged(nameof(TitleColor));
        OnPropertyChanged(nameof(HasCustomTitleColor));
    }
}
