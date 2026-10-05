using System.Collections.ObjectModel;
using HighshoreCairn.Models;

namespace HighshoreCairn.ViewModels;

/// <summary>A checkable entry of a filter menu (an assignee, a priority, a tag, a column).</summary>
public class FilterOption : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public FilterOption(string id, string name, string color, Action changed)
    {
        Id = id;
        Name = name;
        Color = color;
        _changed = changed;
    }

    public string Id { get; }
    public string Name { get; }
    public string Color { get; }
    public string MenuLabel => MenuText.Escape(Name);

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) _changed(); }
    }

    /// <summary>Changes the selection without notifying the owner (used for bulk updates).</summary>
    public void SetSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Value + label pair for combo boxes bound to an enum.</summary>
public class EnumOption<T>
{
    public EnumOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }
    public string Label { get; }
}

/// <summary>State of the filter toolbar + the filtering and sorting logic.</summary>
public class FilterViewModel : ObservableObject
{
    public const string UnassignedId = "";

    private bool _suspended;

    /// <summary>Raised whenever the visible cards must be recomputed.</summary>
    public event Action? Changed;

    public ObservableCollection<FilterOption> Assignees { get; } = new();
    public ObservableCollection<FilterOption> Priorities { get; } = new();
    public ObservableCollection<FilterOption> Tags { get; } = new();
    public ObservableCollection<FilterOption> Columns { get; } = new();

    public IReadOnlyList<EnumOption<DueFilter>> DueOptions { get; } = new[]
    {
        new EnumOption<DueFilter>(DueFilter.Any, "Any due date"),
        new EnumOption<DueFilter>(DueFilter.Overdue, "Overdue"),
        new EnumOption<DueFilter>(DueFilter.ThisWeek, "Due this week"),
        new EnumOption<DueFilter>(DueFilter.ThisMonth, "Due this month"),
        new EnumOption<DueFilter>(DueFilter.NoDueDate, "No due date")
    };

    public IReadOnlyList<EnumOption<SortMode>> SortOptions { get; } = new[]
    {
        new EnumOption<SortMode>(SortMode.Manual, "Manual order"),
        new EnumOption<SortMode>(SortMode.Priority, "Priority"),
        new EnumOption<SortMode>(SortMode.Created, "Creation date"),
        new EnumOption<SortMode>(SortMode.DueDate, "Due date"),
        new EnumOption<SortMode>(SortMode.Assignee, "Assignee")
    };

    public FilterViewModel()
    {
        foreach (var p in new[] { Priority.Urgent, Priority.High, Priority.Medium, Priority.Low, Priority.None })
            Priorities.Add(new FilterOption(p.ToString(), p.ToString(), Palette.PriorityColor(p), OnChanged));
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value ?? "")) OnChanged(); }
    }

    private DueFilter _due = DueFilter.Any;
    public DueFilter Due
    {
        get => _due;
        set { if (SetProperty(ref _due, value)) OnChanged(); }
    }

    private SortMode _sort = SortMode.Manual;
    public SortMode Sort
    {
        get => _sort;
        set { if (SetProperty(ref _sort, value)) OnChanged(); }
    }

    private bool _reverse;
    public bool Reverse
    {
        get => _reverse;
        set { if (SetProperty(ref _reverse, value)) OnChanged(); }
    }

    public string AssigneeHeader => Header("Assignee", Assignees);
    public string PriorityHeader => Header("Priority", Priorities);
    public string TagHeader => Header("Tags", Tags);
    public string ColumnHeader => Header("Columns", Columns);

    /// <summary>True when at least one filter (not the sort) is restricting the cards.</summary>
    public bool IsActive =>
        _searchText.Trim().Length > 0 || _due != DueFilter.Any ||
        Assignees.Any(o => o.IsSelected) || Priorities.Any(o => o.IsSelected) ||
        Tags.Any(o => o.IsSelected) || Columns.Any(o => o.IsSelected);

    private static string Header(string name, IEnumerable<FilterOption> options)
    {
        var count = options.Count(o => o.IsSelected);
        return count == 0 ? name : $"{name} ({count})";
    }

    private void OnChanged()
    {
        if (_suspended) return;
        OnPropertyChanged(nameof(AssigneeHeader));
        OnPropertyChanged(nameof(PriorityHeader));
        OnPropertyChanged(nameof(TagHeader));
        OnPropertyChanged(nameof(ColumnHeader));
        OnPropertyChanged(nameof(IsActive));
        Changed?.Invoke();
    }

    /// <summary>Runs several changes and notifies only once at the end.</summary>
    private void Batch(Action action)
    {
        _suspended = true;
        try { action(); }
        finally { _suspended = false; }
        OnChanged();
    }

    // ------------------------------------------------------------------ options

    /// <summary>Rebuilds the option lists from the project data, keeping the current selection.</summary>
    public void SetOptions(IEnumerable<Account> members, IEnumerable<TagDef> tags, IEnumerable<ColumnData> columns)
    {
        Batch(() =>
        {
            Rebuild(Assignees, members.Select(m => (m.Id, m.Username, m.AvatarColor))
                .Append((UnassignedId, "Unassigned", "#8D8D8D")));
            Rebuild(Tags, tags.Select(t => (t.Id, t.Name, t.Color)));
            Rebuild(Columns, columns.Select(c => (c.Id, c.Name, "#8D8D8D")));
        });
    }

    private void Rebuild(ObservableCollection<FilterOption> target, IEnumerable<(string Id, string Name, string Color)> items)
    {
        var selected = target.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();
        target.Clear();
        foreach (var (id, name, color) in items)
        {
            var option = new FilterOption(id, name, color, OnChanged);
            option.SetSilently(selected.Contains(id));
            target.Add(option);
        }
    }

    public void Clear()
    {
        Batch(() =>
        {
            SearchText = "";
            Due = DueFilter.Any;
            foreach (var o in Assignees.Concat(Priorities).Concat(Tags).Concat(Columns)) o.SetSilently(false);
        });
    }

    // ------------------------------------------------------------------ saved filters

    public SavedFilter ToSaved(string name) => new()
    {
        Name = name,
        Search = SearchText.Trim(),
        AssigneeIds = Assignees.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
        Priorities = Priorities.Where(o => o.IsSelected).Select(o => Enum.Parse<Priority>(o.Id)).ToList(),
        TagIds = Tags.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
        ColumnIds = Columns.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
        Due = Due,
        Sort = Sort,
        Reverse = Reverse
    };

    public void Apply(SavedFilter saved)
    {
        Batch(() =>
        {
            SearchText = saved.Search ?? "";
            Due = saved.Due;
            Sort = saved.Sort;
            Reverse = saved.Reverse;
            foreach (var o in Assignees) o.SetSilently(saved.AssigneeIds.Contains(o.Id));
            foreach (var o in Priorities) o.SetSilently(saved.Priorities.Any(p => p.ToString() == o.Id));
            foreach (var o in Tags) o.SetSilently(saved.TagIds.Contains(o.Id));
            foreach (var o in Columns) o.SetSilently(saved.ColumnIds.Contains(o.Id));
        });
    }

    // ------------------------------------------------------------------ logic

    /// <summary>Column (status) filter: with no column selected every column is shown.</summary>
    public bool ColumnVisible(string columnId)
    {
        var selected = Columns.Where(o => o.IsSelected).ToList();
        return selected.Count == 0 || selected.Any(o => o.Id == columnId);
    }

    public bool Matches(CardData card, bool inDoneColumn, DateTime now)
    {
        var search = _searchText.Trim();
        if (search.Length > 0)
        {
            // Every word must appear in the title or in the description.
            foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!card.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) &&
                    !(card.Description ?? "").Contains(word, StringComparison.CurrentCultureIgnoreCase))
                    return false;
            }
        }

        var assignees = Assignees.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (assignees.Count > 0 && !assignees.Contains(card.AssigneeId ?? UnassignedId)) return false;

        var priorities = Priorities.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (priorities.Count > 0 && !priorities.Contains(card.Priority.ToString())) return false;

        var tags = Tags.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (tags.Count > 0 && !tags.Any(card.TagIds.Contains)) return false;

        return MatchesDue(card, _due, inDoneColumn, now);
    }

    public static bool MatchesDue(CardData card, DueFilter filter, bool inDoneColumn, DateTime now)
    {
        switch (filter)
        {
            case DueFilter.Any:
                return true;
            case DueFilter.NoDueDate:
                return card.DueDate is null;
            case DueFilter.Overdue:
                return IsOverdue(card, inDoneColumn, now);
            case DueFilter.ThisWeek:
            {
                if (card.DueDate is null) return false;
                var start = StartOfWeek(now);
                return card.DueDate.Value >= start && card.DueDate.Value < start.AddDays(7);
            }
            case DueFilter.ThisMonth:
                return card.DueDate is { } due && due.Year == now.Year && due.Month == now.Month;
            default:
                return true;
        }
    }

    public static bool IsOverdue(CardData card, bool inDoneColumn, DateTime now) =>
        !inDoneColumn && card.DueMoment is { } moment && moment < now;

    /// <summary>Monday 00:00 of the week that contains <paramref name="now"/>.</summary>
    public static DateTime StartOfWeek(DateTime now)
    {
        var diff = ((int)now.DayOfWeek + 6) % 7; // Monday = 0 ... Sunday = 6
        return now.Date.AddDays(-diff);
    }

    /// <summary>Applies the selected sort. "Manual" keeps the persisted order of the column.</summary>
    public List<CardData> SortCards(IEnumerable<CardData> cards, Func<string?, string?> assigneeName)
    {
        var list = cards.ToList();
        IEnumerable<CardData> sorted = _sort switch
        {
            SortMode.Priority => list.OrderByDescending(c => (int)c.Priority),
            SortMode.Created => list.OrderByDescending(c => c.CreatedAt),
            SortMode.DueDate => list.OrderBy(c => c.DueDate is null ? 1 : 0).ThenBy(c => c.DueMoment),
            SortMode.Assignee => list.OrderBy(c => assigneeName(c.AssigneeId) is null ? 1 : 0)
                .ThenBy(c => assigneeName(c.AssigneeId), StringComparer.CurrentCultureIgnoreCase),
            _ => list
        };
        var result = sorted.ToList(); // OrderBy is stable: ties keep the manual order
        if (_reverse) result.Reverse();
        return result;
    }
}
