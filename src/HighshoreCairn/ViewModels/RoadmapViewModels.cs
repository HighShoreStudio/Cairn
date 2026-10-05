using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

// ====================================================================== icon picker

/// <summary>An icon of the picker: "ion:name" for the built-in set.</summary>
public class IconItem
{
    public IconItem(string name)
    {
        Name = name;
        Key = IconLibrary.BuiltInPrefix + name;
    }

    public string Name { get; }
    public string Key { get; }
}

/// <summary>
/// Dialog to choose the icon of a milestone type: one of the built-in icons (searchable, in three
/// styles) or a picture / SVG file of the user, which is copied into the project.
/// </summary>
public class IconPickerViewModel : DialogViewModel
{
    public const int MaxShown = 360;

    private readonly IconLibrary _library;
    private readonly IDialogService _dialogs;
    private readonly Func<string, string>? _importFile;

    /// <param name="importFile">Copies an external file into the project and returns its "file:name" key.</param>
    public IconPickerViewModel(IconLibrary library, IDialogService dialogs, string? current, string iconFolder, Func<string, string>? importFile)
    {
        _library = library;
        _dialogs = dialogs;
        _importFile = importFile;
        IconFolder = iconFolder;
        Result = current;

        var name = IconLibrary.BuiltInName(current);
        _variant = name != null ? IconLibrary.VariantOf(name) : "Outline";
        OkCommand = new RelayCommand(() => { if (Selected != null) { Result = Selected.Key; Close(true); } });
        CancelCommand = new RelayCommand(() => Close(false));
        BrowseCommand = new RelayCommand(Browse);
        Apply();
        Selected = Items.FirstOrDefault(i => i.Key == current) ?? Items.FirstOrDefault();
    }

    /// <summary>Where the pictures chosen with "Browse" live (the preview of file icons reads from here).</summary>
    public string IconFolder { get; }

    /// <summary>"ion:name" or "file:name" after OK / Browse.</summary>
    public string? Result { get; private set; }

    public IReadOnlyList<string> Variants { get; } = new[] { "Outline", "Filled", "Sharp" };

    private string _variant;
    public string Variant
    {
        get => _variant;
        set { if (SetProperty(ref _variant, value ?? "Outline")) Apply(); }
    }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value ?? "")) Apply(); }
    }

    public ObservableCollection<IconItem> Items { get; } = new();

    private IconItem? _selected;
    public IconItem? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool CanBrowse => _importFile != null;

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }

    private void Apply()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = _library.Names
            .Where(n => IconLibrary.VariantOf(n) == _variant)
            .Where(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var keep = Selected?.Key ?? Result;
        var shown = matching.Take(MaxShown).ToList();
        // The icon in use is always in the list, also when the list is cut short.
        if (IconLibrary.BuiltInName(keep) is { } current && matching.Contains(current) && !shown.Contains(current))
        {
            shown.RemoveAt(shown.Count - 1);
            shown.Insert(0, current);
        }
        Items.Clear();
        foreach (var name in shown) Items.Add(new IconItem(name));
        Selected = Items.FirstOrDefault(i => i.Key == keep) ?? Items.FirstOrDefault();
        Summary = matching.Count == 0 ? "No icon matches."
            : matching.Count > MaxShown ? $"Showing {MaxShown} of {matching.Count} icons: type to narrow the list."
            : $"{matching.Count} icons";
    }

    private void Browse()
    {
        if (_importFile is null) return;
        var file = _dialogs.PickFile("Pictures and SVG (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.svg");
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            if (Path.GetExtension(file).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // Only simple, single-color SVG files can be drawn: check before copying.
                var icon = SvgParser.Parse(File.ReadAllText(file));
                if (icon.Shapes.Count == 0) throw new FormatException("The file contains nothing that can be drawn.");
            }
            Result = _importFile(file);
            Close(true);
        }
        catch (Exception ex)
        {
            _dialogs.Error("This file cannot be used as an icon.\n\n" + ex.Message);
        }
    }
}

// ====================================================================== settings rows

/// <summary>A milestone type as edited in the Roadmap tab of the project settings.</summary>
public class MilestoneTypeRow : ObservableObject
{
    public MilestoneTypeRow(MilestoneType type, string iconFolder)
    {
        Id = type.Id;
        _name = type.Name;
        _icon = type.Icon;
        _color = type.Color;
        _level = Math.Clamp(type.Level, 1, 5);
        IconFolder = iconFolder;
    }

    public string Id { get; }
    public string IconFolder { get; }

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _icon;
    public string Icon { get => _icon; set => SetProperty(ref _icon, string.IsNullOrEmpty(value) ? MilestoneType.DefaultIcon : value); }

    private string _color;
    public string Color { get => _color; set => SetProperty(ref _color, value); }

    private int _level;
    /// <summary>1-5: the line of the Milestones row (lower = higher up).</summary>
    public int Level { get => _level; set => SetProperty(ref _level, Math.Clamp(value, 1, 5)); }

    public IReadOnlyList<int> Levels { get; } = new[] { 1, 2, 3, 4, 5 };
    public IReadOnlyList<string> Colors => Palette.Colors;

    public MilestoneType ToModel() => new()
    {
        Id = Id, Name = (Name ?? "").Trim(), Icon = Icon, Color = Color, Level = Level
    };
}

// ====================================================================== editors

/// <summary>Dialog to create or edit a milestone of the roadmap.</summary>
public class MilestoneEditorViewModel : DialogViewModel
{
    private readonly Milestone? _existing;
    private readonly RoadmapSettings _settings;
    private readonly IReadOnlyList<Milestone> _all;
    private readonly IDialogService _dialogs;

    public MilestoneEditorViewModel(Milestone? existing, RoadmapSettings settings, IReadOnlyList<Milestone> all,
        IDialogService dialogs, DateTime defaultStart, string iconFolder)
    {
        _existing = existing;
        _settings = settings;
        _all = all;
        _dialogs = dialogs;
        IconFolder = iconFolder;
        Types = settings.MilestoneTypes.ToList();

        _name = existing?.Name ?? "";
        _type = Types.FirstOrDefault(t => t.Id == existing?.TypeId) ?? Types.FirstOrDefault();
        _start = existing?.Start.Date ?? defaultStart.Date;
        _end = existing?.End.Date ?? defaultStart.Date.AddDays(13);
        _note = existing?.Note ?? "";

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public bool IsNew => _existing is null;
    public string WindowTitle => IsNew ? "New milestone" : "Edit milestone";
    public string IconFolder { get; }
    public IReadOnlyList<MilestoneType> Types { get; }
    public bool HasTypes => Types.Count > 0;

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private MilestoneType? _type;
    public MilestoneType? Type { get => _type; set => SetProperty(ref _type, value); }

    private DateTime? _start;
    /// <summary>The day the work on the release starts.</summary>
    public DateTime? Start { get => _start; set => SetProperty(ref _start, value); }

    private DateTime? _end;
    /// <summary>The release day.</summary>
    public DateTime? End { get => _end; set => SetProperty(ref _end, value); }

    private string _note;
    public string Note { get => _note; set => SetProperty(ref _note, value); }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The milestone to store; null when the user accepted that it gets deleted (it would be hidden).</summary>
    public Milestone? Result { get; private set; }

    /// <summary>Other milestones that the user accepted to delete because this one hides them completely.</summary>
    public IReadOnlyList<string> RemovedIds { get; private set; } = Array.Empty<string>();

    /// <summary>The edited milestone itself must be deleted (it would be completely hidden).</summary>
    public bool DeleteSelf { get; private set; }

    private void Save()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "The name is required.";
            return;
        }
        if (Start is null || End is null)
        {
            Error = "Both dates are required.";
            return;
        }
        if (End.Value.Date < Start.Value.Date)
        {
            Error = "The release date cannot be before the start date.";
            return;
        }

        var draft = new Milestone
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Name = Name.Trim(),
            TypeId = Type?.Id ?? "",
            Start = DateTime.SpecifyKind(Start.Value.Date, DateTimeKind.Unspecified),
            End = DateTime.SpecifyKind(End.Value.Date, DateTimeKind.Unspecified),
            Note = (Note ?? "").Trim()
        };

        var (identical, hidden) = MilestoneRules.Check(draft, _all, _settings);
        if (identical != null)
        {
            Error = $"'{identical.Name}' already has exactly these dates.";
            _dialogs.Error(
                $"Two milestones on the same line cannot have the same dates.\n\n'{identical.Name}' already goes from " +
                $"{identical.Start:dd MMM yyyy} to {identical.End:dd MMM yyyy}: change at least one of the two dates.",
                "Milestone");
            return;
        }

        if (hidden.Count > 0)
        {
            var self = hidden.Any(m => m.Id == draft.Id);
            var others = hidden.Where(m => m.Id != draft.Id).ToList();
            var message = self && others.Count == 0
                ? $"With these dates '{draft.Name}' would be completely covered by other milestones of its line, so it could never be seen.\n\n" +
                  "Continue anyway? The milestone will be deleted.\n\nChoose No to go back and change its dates."
                : $"With these dates {(others.Count == 1 ? "this milestone" : "these milestones")} would be completely covered and could never be seen:\n\n" +
                  string.Join("\n", hidden.Select(m => $"  •  {m.Name}  ({m.Start:dd MMM yyyy} – {m.End:dd MMM yyyy})")) +
                  $"\n\nContinue anyway? {(hidden.Count == 1 ? "It" : "They")} will be deleted.\n\nChoose No to go back and change the dates.";
            if (!_dialogs.Confirm(message, "Hidden milestone")) return;
            RemovedIds = others.Select(m => m.Id).ToList();
            DeleteSelf = self;
        }

        Result = DeleteSelf ? null : draft;
        Close(true);
    }
}

/// <summary>Dialog to create or edit a task of the "Custom" row of the roadmap.</summary>
public class RoadmapItemEditorViewModel : DialogViewModel
{
    private readonly RoadmapItem? _existing;

    public RoadmapItemEditorViewModel(RoadmapItem? existing, DateTime defaultStart, string rowName)
    {
        _existing = existing;
        RowName = rowName;
        _name = existing?.Name ?? "";
        _start = existing?.Start.Date ?? defaultStart.Date;
        _due = existing?.Due?.Date;
        _end = existing?.End?.Date;
        _note = existing?.Note ?? "";
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        ClearDueCommand = new RelayCommand(() => Due = null);
        ClearEndCommand = new RelayCommand(() => End = null);
    }

    public bool IsNew => _existing is null;
    public string RowName { get; }
    public string WindowTitle => (IsNew ? "New task" : "Edit task") + $" ({RowName})";

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private DateTime? _start;
    public DateTime? Start { get => _start; set => SetProperty(ref _start, value); }

    private DateTime? _due;
    public DateTime? Due
    {
        get => _due;
        set { if (SetProperty(ref _due, value)) OnPropertyChanged(nameof(HasDue)); }
    }

    public bool HasDue => _due != null;

    private DateTime? _end;
    public DateTime? End
    {
        get => _end;
        set { if (SetProperty(ref _end, value)) OnPropertyChanged(nameof(HasEnd)); }
    }

    public bool HasEnd => _end != null;

    private string _note;
    public string Note { get => _note; set => SetProperty(ref _note, value); }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearDueCommand { get; }
    public ICommand ClearEndCommand { get; }

    public RoadmapItem? Result { get; private set; }

    private void Save()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "The name is required.";
            return;
        }
        if (Start is null)
        {
            Error = "The start date is required.";
            return;
        }
        if (Due is null && End is null)
        {
            Error = "Set a due date, an end date, or both.";
            return;
        }
        if (End != null && End.Value.Date < Start.Value.Date)
        {
            Error = "The end date cannot be before the start date.";
            return;
        }

        static DateTime Day(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        Result = new RoadmapItem
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Name = Name.Trim(),
            Start = Day(Start.Value),
            Due = Due is null ? null : Day(Due.Value),
            End = End is null ? null : Day(End.Value),
            Note = (Note ?? "").Trim()
        };
        Close(true);
    }
}

// ====================================================================== the roadmap window

/// <summary>
/// The Roadmap window of a project: the rows (milestones, custom tasks, one row per tag), the period on
/// screen and the selection. Drawing and mouse handling are done by the RoadmapView control, which
/// reads <see cref="Rows"/> and the view window and calls the methods of this class.
/// </summary>
public class RoadmapViewModel : WindowViewModel
{
    private readonly BoardViewModel _board;
    private readonly IDialogService _dialogs;
    private readonly RoadmapService _service;
    private readonly Func<DateTime> _clock;

    public RoadmapViewModel(BoardViewModel board, IDialogService dialogs, Func<DateTime>? clock = null)
    {
        _board = board;
        _dialogs = dialogs;
        _clock = clock ?? (() => DateTime.Now);
        _service = new RoadmapService(board.Project.Folder);

        try { Data = _service.Load(); }
        catch (Exception ex)
        {
            // Never save over a file that could not be read (a merge conflict is enough): keep it under another name.
            Data = new RoadmapData();
            string? kept = null;
            try { kept = _service.SetAsideUnreadable(_clock()); }
            catch { _readOnly = true; }
            _dialogs.Error("The roadmap file could not be read: an empty roadmap is shown.\n\n" + ex.Message + "\n\n" +
                           (kept != null ? $"The file was kept as '{kept}' in the project folder."
                               : "The file is left as it is: changes made here are not saved until it is fixed or removed."));
        }
        Timeline = new RoadmapTimeline(_clock(), Data.Months);
        _viewFrom = Timeline.From;
        _viewDays = (Timeline.To - Timeline.From).TotalDays;

        // The months of the label do not move while the free view is on.
        PreviousCommand = new RelayCommand(() => { if (_isFreeView) return; Timeline.Previous(); OnTimelineChanged(); });
        NextCommand = new RelayCommand(() => { if (_isFreeView) return; Timeline.Next(); OnTimelineChanged(); });
        TodayCommand = new RelayCommand(GoToToday);
        ToggleFreeViewCommand = new RelayCommand(() => IsFreeView = !IsFreeView);
        MonthsUpCommand = new RelayCommand(() => Months++);
        MonthsDownCommand = new RelayCommand(() => Months--);
        AddMilestoneCommand = new RelayCommand(() => EditMilestone(null, null));
        AddCustomCommand = new RelayCommand(() => EditItem(null, null));
        EditSelectedCommand = new RelayCommand(() => Activate(SelectedId, SelectedKind));
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        SettingsCommand = new RelayCommand(() => _board.OpenSettings("Roadmap"));
        CloseCommand = new RelayCommand(RequestClose);

        Refresh();
    }

    public RoadmapData Data { get; }
    public RoadmapSettings Settings => _board.Project.Info.Settings.Roadmap;
    public RoadmapTimeline Timeline { get; }
    public string ProjectName => _board.ProjectName;
    public string WindowTitle => $"Roadmap – {_board.ProjectName}";
    public string IconFolder => _service.IconsFolder;
    public DateTime Now => _clock();
    public bool IsDark => _board.IsDark;

    /// <summary>The rows changed, or the period on screen did: the control must redraw.</summary>
    public event Action? Changed;

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand ToggleFreeViewCommand { get; }
    public ICommand MonthsUpCommand { get; }
    public ICommand MonthsDownCommand { get; }
    public ICommand AddMilestoneCommand { get; }
    public ICommand AddCustomCommand { get; }
    public ICommand EditSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CloseCommand { get; }

    public override void OnOpened()
    {
        base.OnOpened();
        _board.Changed += Refresh;
    }

    public override void OnClosed()
    {
        base.OnClosed();
        _board.Changed -= Refresh;
    }

    // ------------------------------------------------------------------ rows

    public IReadOnlyList<RoadmapRow> Rows { get; private set; } = Array.Empty<RoadmapRow>();

    public bool ShowMilestones => Settings.ShowMilestones;
    public bool ShowCustom => Settings.ShowCustom;
    public string AddMilestoneText => "+ " + SingularOf(Settings.MilestonesName, "Milestone");
    public string AddCustomText => "+ " + Settings.CustomName + " task";

    private static string SingularOf(string name, string fallback)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return fallback;
        return name.Length > 3 && name.EndsWith('s') ? name[..^1] : name;
    }

    /// <summary>Recomputes the rows from the board, the roadmap data and the settings.</summary>
    public void Refresh()
    {
        Rows = RoadmapLayout.Build(_board.Data, Data, Settings, _clock());
        // The selected bar may be gone.
        if (_selectedId != null && Rows.All(r => r.Lanes.All(l => l.All(b => b.Id != _selectedId || b.Kind != _selectedKind))))
            ClearSelection(raise: false);
        OnPropertyChanged(nameof(ShowMilestones));
        OnPropertyChanged(nameof(ShowCustom));
        OnPropertyChanged(nameof(AddMilestoneText));
        OnPropertyChanged(nameof(AddCustomText));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke();
    }

    public string Summary
    {
        get
        {
            var tasks = Rows.Where(r => r.Kind is RoadmapRowKind.Tag or RoadmapRowKind.Untagged)
                .SelectMany(r => r.Lanes.SelectMany(l => l)).Select(b => b.Id).Distinct().Count();
            return $"{tasks} task(s)  ·  {Data.Milestones.Count} milestone(s)  ·  {Data.Items.Count} custom";
        }
    }

    private bool _readOnly;   // the file on disk could not be read nor set aside: it must not be replaced

    private void SaveData()
    {
        if (_readOnly) return;
        try { _service.Save(Data); }
        catch (Exception ex) { _dialogs.Error("The roadmap could not be saved.\n\n" + ex.Message); }
    }

    /// <summary>Deletes milestones without asking (the question was already answered) and redraws.</summary>
    public void RemoveMilestones(ICollection<string> ids)
    {
        if (Data.Milestones.RemoveAll(m => ids.Contains(m.Id)) == 0) return;
        if (_selectedId != null && _selectedKind == RoadmapBarKind.Milestone && ids.Contains(_selectedId)) ClearSelection(raise: false);
        SaveData();
        Refresh();
    }

    /// <summary>The eye button of a row: closes or reopens it.</summary>
    public void ToggleCollapsed(string key)
    {
        if (!Data.Collapsed.Remove(key)) Data.Collapsed.Add(key);
        SaveData();
        Refresh();
    }

    public bool CanMoveRow(string key, int delta)
    {
        var order = RoadmapLayout.OrderedTags(_board.Data, Data).Select(t => t.Id).ToList();
        var index = order.IndexOf(key);
        return index >= 0 && index + delta >= 0 && index + delta < order.Count;
    }

    /// <summary>Moves the row of a tag up (-1) or down (+1).</summary>
    public void MoveRow(string key, int delta)
    {
        if (!RoadmapLayout.MoveTag(_board.Data, Data, key, delta)) return;
        SaveData();
        Refresh();
    }

    /// <summary>Drag and drop of a tag row: puts it at the given position among the tag rows.</summary>
    public void PlaceRow(string key, int position)
    {
        if (!RoadmapLayout.PlaceTag(_board.Data, Data, key, position)) return;
        SaveData();
        Refresh();
    }

    // ------------------------------------------------------------------ period on screen

    private DateTime _viewFrom;
    /// <summary>The moment at the left edge of the timeline.</summary>
    public DateTime ViewFrom => _viewFrom;

    private double _viewDays;
    /// <summary>How many days fit in the width of the timeline.</summary>
    public double ViewDays => _viewDays;

    public DateTime ViewTo => _viewFrom.AddDays(_viewDays);

    // The free view stays well inside what a date can hold (two years of room on both sides).
    private static readonly DateTime FirstView = new(3, 1, 1), LastView = new(9990, 1, 1);

    private static DateTime Limited(DateTime from) => from < FirstView ? FirstView : from > LastView ? LastView : from;

    public string RangeLabel => Timeline.Label;

    /// <summary>1-12 months on screen (while the free view is off).</summary>
    public int Months
    {
        get => Timeline.Months;
        set
        {
            var months = Math.Clamp(value, 1, 12);
            if (months == Timeline.Months) { OnPropertyChanged(); return; }
            Timeline.SetMonths(months);
            Data.Months = months;
            SaveData();
            OnPropertyChanged();
            OnTimelineChanged();
        }
    }

    public IReadOnlyList<int> MonthChoices { get; } = Enumerable.Range(1, 12).ToList();

    private void OnTimelineChanged()
    {
        OnPropertyChanged(nameof(RangeLabel));
        if (_isFreeView) return; // the label never follows the free view, and the free view keeps its own period
        _viewFrom = Timeline.From;
        _viewDays = (Timeline.To - Timeline.From).TotalDays;
        Changed?.Invoke();
    }

    private bool _isFreeView;
    /// <summary>
    /// Free view: the wheel zooms (from one week to two years on screen) and the view can be dragged.
    /// Turning it off goes back to the months of the label.
    /// </summary>
    public bool IsFreeView
    {
        get => _isFreeView;
        set
        {
            if (!SetProperty(ref _isFreeView, value)) return;
            OnPropertyChanged(nameof(IsMonthView));
            if (!value)
            {
                _viewFrom = Timeline.From;
                _viewDays = (Timeline.To - Timeline.From).TotalDays;
            }
            Changed?.Invoke();
        }
    }

    public bool IsMonthView => !_isFreeView;

    /// <summary>Free view: zooms around a date (factor &lt; 1 = closer).</summary>
    public void Zoom(double factor, DateTime pivot)
    {
        if (!_isFreeView || factor <= 0) return;
        var days = Math.Clamp(_viewDays * factor, RoadmapLayout.MinViewDays, RoadmapLayout.MaxViewDays);
        if (Math.Abs(days - _viewDays) < 1e-9) return;
        var ratio = days / _viewDays;
        try { _viewFrom = Limited(pivot - TimeSpan.FromDays((pivot - _viewFrom).TotalDays * ratio)); }
        catch (ArgumentOutOfRangeException) { return; }
        _viewDays = days;
        Changed?.Invoke();
    }

    /// <summary>Free view: moves the period by a number of days (negative = to the past).</summary>
    public void Pan(double days)
    {
        if (!_isFreeView || days == 0) return;
        try { _viewFrom = Limited(_viewFrom.AddDays(days)); }
        catch (ArgumentOutOfRangeException) { return; }
        Changed?.Invoke();
    }

    /// <summary>Arrow keys in the free view: a small step, or a big one with Shift.</summary>
    public void Step(int direction, bool fast) => Pan(direction * _viewDays * (fast ? 0.25 : 0.03));

    private void GoToToday()
    {
        var today = _clock();
        if (_isFreeView)
        {
            // Today near the left edge, with a little room before it. The months of the label stay as they are.
            _viewFrom = Limited(today.Date.AddDays(-_viewDays * 0.04));
            Changed?.Invoke();
            return;
        }
        Timeline.Today(today);
        OnTimelineChanged();
    }

    private string _hoverText = "";
    /// <summary>The date under the mouse ("02 May 2026 - 18w"), shown at the top of the window.</summary>
    public string HoverText { get => _hoverText; private set => SetProperty(ref _hoverText, value); }

    public void SetHover(DateTime? date) =>
        HoverText = date is null ? "" : RoadmapLayout.FormatHover(date.Value, Settings.ShowWeekNumber);

    // ------------------------------------------------------------------ selection

    private string? _selectedId;
    public string? SelectedId => _selectedId;

    private RoadmapBarKind _selectedKind;
    public RoadmapBarKind SelectedKind => _selectedKind;

    public bool HasSelection => _selectedId != null;
    /// <summary>Only milestones and custom tasks can be deleted here; real tasks are deleted from the Kanban board.</summary>
    public bool CanDeleteSelection => _selectedId != null && _selectedKind != RoadmapBarKind.Card;

    public bool IsSelected(RoadmapBar bar) => _selectedId == bar.Id && _selectedKind == bar.Kind;

    public void Select(RoadmapBar? bar)
    {
        if (bar is null)
        {
            ClearSelection();
            return;
        }
        if (IsSelected(bar)) return;
        _selectedId = bar.Id;
        _selectedKind = bar.Kind;
        RaiseSelection();
        Changed?.Invoke();
    }

    public void ClearSelection(bool raise = true)
    {
        if (_selectedId is null) return;
        _selectedId = null;
        RaiseSelection();
        if (raise) Changed?.Invoke();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    public string SelectionText
    {
        get
        {
            if (_selectedId is null) return "";
            switch (_selectedKind)
            {
                case RoadmapBarKind.Milestone when Data.Milestones.FirstOrDefault(m => m.Id == _selectedId) is { } m:
                    return $"{m.Name}  ·  {m.Start:dd MMM yyyy} – {m.End:dd MMM yyyy}";
                case RoadmapBarKind.Custom when Data.Items.FirstOrDefault(i => i.Id == _selectedId) is { } i:
                    return $"{i.Name}  ·  start {i.Start:dd MMM yyyy}" + Dated("due", i.Due) + Dated("end", i.End);
                case RoadmapBarKind.Card when _board.Index.Find(_selectedId) is { } c:
                    return $"{c.Title}" + Dated("start", c.StartDate)
                           + Dated("due", c.DueDate) + Dated(c.FinishDate.HasValue ? "finish" : "end", c.FinishDate ?? c.EndDate)
                           + "  ·  double-click to open the task";
                default:
                    return "";
            }
        }
    }

    private static string Dated(string label, DateTime? date) => date.HasValue ? $"  ·  {label} {date:dd MMM yyyy}" : "";

    /// <summary>The colors of the selected bar for the current theme (background, text).</summary>
    public (string Back, string Text) SelectionColors => IsDark
        ? (Settings.SelectionBackDark, Settings.SelectionTextDark)
        : (Settings.SelectionBackLight, Settings.SelectionTextLight);

    // ------------------------------------------------------------------ actions on bars

    /// <summary>Double click: opens the task dialog, or the editor of the milestone / custom task.</summary>
    public void Activate(string? id, RoadmapBarKind kind)
    {
        if (id is null) return;
        switch (kind)
        {
            case RoadmapBarKind.Card:
                _board.EditCardById(id);
                break;
            case RoadmapBarKind.Custom:
                EditItem(Data.Items.FirstOrDefault(i => i.Id == id), null);
                break;
            case RoadmapBarKind.Milestone:
                EditMilestone(Data.Milestones.FirstOrDefault(m => m.Id == id), null);
                break;
        }
    }

    /// <summary>Creates (existing = null) or edits a milestone. <paramref name="at"/>: the day a new one starts.</summary>
    public void EditMilestone(Milestone? existing, DateTime? at)
    {
        if (existing is null && Settings.MilestoneTypes.Count == 0)
        {
            _dialogs.Info("There are no milestone types: add one in Settings > Roadmap first.", "Roadmap");
            return;
        }
        var editor = new MilestoneEditorViewModel(existing, Settings, Data.Milestones, _dialogs, at ?? DefaultDate(), IconFolder);
        if (_dialogs.ShowDialog(editor) != true) return;

        Data.Milestones.RemoveAll(m => editor.RemovedIds.Contains(m.Id));
        if (editor.DeleteSelf)
        {
            if (existing != null) Data.Milestones.Remove(existing);
        }
        else if (editor.Result != null)
        {
            var index = existing is null ? -1 : Data.Milestones.IndexOf(existing);
            if (index >= 0) Data.Milestones[index] = editor.Result;
            else Data.Milestones.Add(editor.Result);
        }
        SaveData();
        Refresh();
    }

    /// <summary>Creates (existing = null) or edits a task of the Custom row.</summary>
    public void EditItem(RoadmapItem? existing, DateTime? at)
    {
        var editor = new RoadmapItemEditorViewModel(existing, at ?? DefaultDate(), Settings.CustomName);
        if (_dialogs.ShowDialog(editor) != true || editor.Result is null) return;
        var index = existing is null ? -1 : Data.Items.IndexOf(existing);
        if (index >= 0) Data.Items[index] = editor.Result;
        else Data.Items.Add(editor.Result);
        SaveData();
        Refresh();
    }

    /// <summary>New items start today when today is on screen, otherwise at the start of the visible period.</summary>
    private DateTime DefaultDate()
    {
        var today = _clock().Date;
        return today >= _viewFrom && today < ViewTo ? today : _viewFrom.Date;
    }

    /// <summary>Deletes a milestone or a custom task (never a real task).</summary>
    public void Delete(string id, RoadmapBarKind kind)
    {
        string? name = kind switch
        {
            RoadmapBarKind.Milestone => Data.Milestones.FirstOrDefault(m => m.Id == id)?.Name,
            RoadmapBarKind.Custom => Data.Items.FirstOrDefault(i => i.Id == id)?.Name,
            _ => null
        };
        if (name is null) return;
        if (!_dialogs.Confirm($"Delete '{name}' from the roadmap?", "Delete")) return;
        if (kind == RoadmapBarKind.Milestone) Data.Milestones.RemoveAll(m => m.Id == id);
        else Data.Items.RemoveAll(i => i.Id == id);
        SaveData();
        Refresh();
    }

    private void DeleteSelected()
    {
        if (CanDeleteSelection) Delete(_selectedId!, _selectedKind);
    }

    /// <summary>Real tasks: removes the start and the finish date, so the task leaves the roadmap (and nothing else).</summary>
    public void RemoveDates(string cardId)
    {
        var card = _board.Index.Find(cardId);
        if (card is null) return;
        if (!_dialogs.Confirm(
                $"Remove the start and the finish date of '{card.Title}'?\n\nThe task disappears from the roadmap; " +
                "it stays on the Kanban board with its due and end date.", "Remove Start/Finish dates")) return;
        _board.ClearRoadmapDates(cardId);
    }
}
