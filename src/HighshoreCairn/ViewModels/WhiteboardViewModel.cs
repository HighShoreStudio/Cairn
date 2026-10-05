using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

public enum WbTool { Select, Pan, Text, Note, Pen, Line, Polyline, Rectangle, Ellipse, Triangle, Polygon, Connector }

/// <summary>A button of the whiteboard tool bar.</summary>
public class ToolOption : ObservableObject
{
    public ToolOption(WbTool tool, string name, string hint, string icon, Action<WbTool> select)
    {
        Tool = tool;
        Name = name;
        Hint = hint;
        Icon = icon;
        SelectCommand = new RelayCommand(() => select(tool));
    }

    public WbTool Tool { get; }
    public string Name { get; }
    /// <summary>Short instructions shown in the status line while the tool is active.</summary>
    public string Hint { get; }
    /// <summary>Icon as path data (WPF geometry mini-language, drawn in a 20x20 box).</summary>
    public string Icon { get; }
    public ICommand SelectCommand { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>A task or a document referenced by a whiteboard element (listed under the preview in the graph view).</summary>
public class WbReference
{
    public WbReference(string kind, string label, bool isMissing, ICommand openCommand, ICommand removeCommand, string? detail = null)
    {
        Kind = kind;
        Label = label;
        IsMissing = isMissing;
        OpenCommand = openCommand;
        RemoveCommand = removeCommand;
        Detail = detail ?? "";
    }

    /// <summary>"task" or "doc".</summary>
    public string Kind { get; }
    public bool IsTask => Kind == "task";
    public bool IsDocument => Kind == "doc";
    public string Label { get; }
    public string Detail { get; }
    public bool IsMissing { get; }
    public string ToolTip => IsTask
        ? (IsMissing ? "The task was deleted or archived." : "Click to show the task on the Kanban board")
        : (IsMissing ? Detail + "\nThe document no longer exists." : Detail + "\nClick to open the document");
    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>
/// State of the whiteboard screen: the open board, the selection, the active tool and the
/// current drawing style. Mouse handling and drawing are done by the WhiteboardCanvas control,
/// which reads <see cref="Data"/> and calls the methods of this class.
/// </summary>
public class WhiteboardViewModel : ObservableObject
{
    private const int MaxUndo = 60;
    public const string NoteColor = "#FFE08A";

    private readonly WhiteboardService _service;
    private readonly IDialogService _dialogs;
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private bool _syncingStyle;
    private bool _loadingBoard;

    private readonly BoardViewModel? _board;

    /// <param name="board">The open project: gives the tasks and the documents an element can reference (null in isolated tests).</param>
    public WhiteboardViewModel(string projectFolder, IDialogService dialogs, BoardViewModel? board = null)
    {
        _service = new WhiteboardService(projectFolder);
        _dialogs = dialogs;
        _board = board;

        Tools = new List<ToolOption>
        {
            new(WbTool.Select, "Select", "Click to select, drag to move. Drag on empty space to select several. Handles: resize and rotate.", "M4,2 L4,16 L8,12.5 L10.8,18 L13,17 L10.3,11.6 L15.5,11.6 Z", SetTool),
            new(WbTool.Pan, "Pan", "Drag to move the view. Tip: the middle mouse button (or Space + drag) pans with any tool, the wheel zooms. The right button opens the menu of an element.", "M10,2 L10,18 M2,10 L18,10 M10,2 L7.5,4.5 M10,2 L12.5,4.5 M10,18 L7.5,15.5 M10,18 L12.5,15.5 M2,10 L4.5,7.5 M2,10 L4.5,12.5 M18,10 L15.5,7.5 M18,10 L15.5,12.5", SetTool),
            new(WbTool.Text, "Text", "Click where the text should go and type. Double-click a text to edit it.", "M4,4 L16,4 M10,4 L10,17 M7.5,17 L12.5,17", SetTool),
            new(WbTool.Note, "Sticky note", "Click to place a sticky note and type.", "M3,3 L17,3 L17,12 L12,17 L3,17 Z M17,12 L12,12 L12,17", SetTool),
            new(WbTool.Pen, "Freehand", "Drag to draw. End near the start point to close the shape.", "M3,14 C5,6 8,6 9,11 C10,16 13,15 17,5", SetTool),
            new(WbTool.Line, "Line", "Drag from start to end. Shift: snap to 15° steps.", "M3,17 L17,3", SetTool),
            new(WbTool.Polyline, "Polyline", "Click to add points. Double-click or Enter to finish, Esc to cancel.", "M3,16 L7,6 L12,13 L17,4", SetTool),
            new(WbTool.Rectangle, "Rectangle", "Drag to draw. Shift: square.", "M3,5 L17,5 L17,15 L3,15 Z", SetTool),
            new(WbTool.Ellipse, "Circle / ellipse", "Drag to draw. Shift: circle.", "M3,10 A7,5.5 0 1 0 17,10 A7,5.5 0 1 0 3,10 Z", SetTool),
            new(WbTool.Triangle, "Triangle", "Drag to draw. Shift: equal sides.", "M10,3 L17,16 L3,16 Z", SetTool),
            new(WbTool.Polygon, "Polygon", "Click to add corners. Double-click or Enter to close the polygon, Esc to cancel.", "M10,2.5 L17.5,8 L14.6,16.8 L5.4,16.8 L2.5,8 Z", SetTool),
            new(WbTool.Connector, "Connect", "Drag from one element to another to link them (or click the first, then the second).", "M6.4,13.6 L13.6,6.4 M3,15 A2,2 0 1 0 7,15 A2,2 0 1 0 3,15 Z M13,5 A2,2 0 1 0 17,5 A2,2 0 1 0 13,5 Z", SetTool)
        };
        Tools[0].IsSelected = true;

        UndoCommand = new RelayCommand(Undo);
        RedoCommand = new RelayCommand(Redo);
        DeleteCommand = new RelayCommand(DeleteSelection);
        GroupCommand = new RelayCommand(GroupSelection);
        UngroupCommand = new RelayCommand(UngroupSelection);
        DuplicateCommand = new RelayCommand(DuplicateSelection);
        FrontCommand = new RelayCommand(() => Reorder(front: true));
        BackCommand = new RelayCommand(() => Reorder(front: false));
        SelectAllCommand = new RelayCommand(SelectAll);
        StrokeAutoCommand = new RelayCommand(() => StrokeColor = null);
        FillNoneCommand = new RelayCommand(() => FillColor = null);
        ZoomInCommand = new RelayCommand(() => ViewRequested?.Invoke("zoomin"));
        ZoomOutCommand = new RelayCommand(() => ViewRequested?.Invoke("zoomout"));
        ZoomResetCommand = new RelayCommand(() => ViewRequested?.Invoke("reset"));
        FitCommand = new RelayCommand(() => ViewRequested?.Invoke("fit"));
        ExportCommand = new RelayCommand(() => ViewRequested?.Invoke("export"));
        AddImageCommand = new RelayCommand(() => ViewRequested?.Invoke("addimage"));
        ToggleGraphCommand = new RelayCommand(() => IsGraphMode = !IsGraphMode);
        ShowOnBoardCommand = new RelayCommand(ShowOnBoard);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite);
        DisconnectCommand = new RelayCommand(DisconnectSelection);
        ViewInGraphCommand = new RelayCommand(ViewInGraph);
        AddCardRefCommand = new RelayCommand(AddCardRef);
        AddDocRefCommand = new RelayCommand(AddDocRef);
        NewBoardCommand = new RelayCommand(NewBoard);
        RenameBoardCommand = new RelayCommand(RenameBoard);
        DeleteBoardCommand = new RelayCommand(DeleteBoard);

        Data = new WhiteboardData();
        ReloadBoards(null);
    }

    public WhiteboardService Service => _service;
    public IDialogService Dialogs => _dialogs;

    /// <summary>The board being edited. The canvas draws exactly this.</summary>
    public WhiteboardData Data { get; private set; }

    /// <summary>Elements or connectors changed: the canvas must redraw everything.</summary>
    public event Action? DocumentChanged;

    /// <summary>The selection changed: the canvas must redraw the selection frames.</summary>
    public event Action? SelectionChanged;

    /// <summary>Requests handled by the canvas: zoomin, zoomout, reset, fit, focus, export, addimage.</summary>
    public event Action<string>? ViewRequested;

    // ------------------------------------------------------------------ boards

    public ObservableCollection<WhiteboardInfo> Boards { get; } = new();

    private WhiteboardInfo? _selectedBoard;
    public WhiteboardInfo? SelectedBoard
    {
        get => _selectedBoard;
        set
        {
            if (!SetProperty(ref _selectedBoard, value) || _loadingBoard || value is null) return;
            LoadBoard(value.Id);
        }
    }

    public ICommand NewBoardCommand { get; }
    public ICommand RenameBoardCommand { get; }
    public ICommand DeleteBoardCommand { get; }

    private void ReloadBoards(string? selectId)
    {
        var list = _service.List();
        if (list.Count == 0)
        {
            var first = _service.Create("Main");
            list.Add(new WhiteboardInfo { Id = first.Id, Name = first.Name });
        }

        _loadingBoard = true;
        Boards.Clear();
        foreach (var info in list) Boards.Add(info);
        var target = Boards.FirstOrDefault(b => b.Id == selectId) ?? Boards[0];
        _selectedBoard = target;
        OnPropertyChanged(nameof(SelectedBoard));
        _loadingBoard = false;

        LoadBoard(target.Id);
    }

    private void LoadBoard(string id)
    {
        try
        {
            Data = _service.Load(id) ?? new WhiteboardData { Id = id };
        }
        catch (Exception ex)
        {
            _dialogs.Error("The whiteboard could not be read.\n\n" + ex.Message);
            Data = new WhiteboardData { Id = id, Name = _selectedBoard?.Name ?? "Whiteboard" };
        }
        _undo.Clear();
        _redo.Clear();
        _graphExtra.Clear();
        SelectedIds.Clear();
        SelectedConnectorId = null;
        StatusText = "";
        RaiseStateChanged();
        DocumentChanged?.Invoke();
        ViewRequested?.Invoke("fit");
        if (_isGraphMode) RebuildGraph();
    }

    private void NewBoard()
    {
        var name = _dialogs.Prompt("New whiteboard", "Name:", $"Whiteboard {Boards.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var data = _service.Create(name);
        ReloadBoards(data.Id);
    }

    private void RenameBoard()
    {
        var name = _dialogs.Prompt("Rename whiteboard", "Name:", Data.Name);
        if (string.IsNullOrWhiteSpace(name) || name == Data.Name) return;
        Data.Name = name;
        Save();
        var id = Data.Id;
        _loadingBoard = true;
        var index = Boards.ToList().FindIndex(b => b.Id == id);
        if (index >= 0) Boards[index] = new WhiteboardInfo { Id = id, Name = name };
        _selectedBoard = index >= 0 ? Boards[index] : _selectedBoard;
        OnPropertyChanged(nameof(SelectedBoard));
        _loadingBoard = false;
    }

    private void DeleteBoard()
    {
        if (!_dialogs.Confirm($"Delete the whiteboard '{Data.Name}' and everything on it?", "Delete whiteboard")) return;
        try { _service.Delete(Data.Id); }
        catch (Exception ex) { _dialogs.Error(ex.Message); }
        ReloadBoards(null);
    }

    // ------------------------------------------------------------------ graph view

    private bool _isGraphMode;
    /// <summary>
    /// Shows the elements linked with the Connect tool as a graph (like the graph of the documentation)
    /// instead of the drawing surface.
    /// </summary>
    public bool IsGraphMode
    {
        get => _isGraphMode;
        set
        {
            if (!SetProperty(ref _isGraphMode, value)) return;
            if (value) RebuildGraph();
            OnPropertyChanged(nameof(IsBoardMode));
        }
    }

    public bool IsBoardMode => !_isGraphMode;

    public ICommand ToggleGraphCommand { get; }
    public ICommand ShowOnBoardCommand { get; }

    private GraphModel _graph = new();
    public GraphModel Graph { get => _graph; private set => SetProperty(ref _graph, value); }

    public string GraphSummary => _graph.Nodes.Count == 0
        ? "Nothing to show yet: link two elements with the Connect tool, mark one with the star, or use \"View in Graph\" on an element."
        : $"{_graph.Nodes.Count} elements · {_graph.Edges.Count} links";

    public bool IsGraphEmpty => _graph.Nodes.Count == 0;

    /// <summary>Elements shown in the graph on request ("View in Graph") although nothing links them.</summary>
    private readonly HashSet<string> _graphExtra = new();

    public void RebuildGraph()
    {
        var model = WhiteboardGraph.Build(Data, _graphExtra);
        model.Run(220);
        model.Alpha = Math.Max(model.Alpha, 0.05);
        Graph = model;
        OnPropertyChanged(nameof(GraphSummary));
        OnPropertyChanged(nameof(IsGraphEmpty));
        SelectGraphNode(_graphNodeId != null && model.Find(_graphNodeId) != null ? _graphNodeId : null);
    }

    private string? _graphNodeId;
    /// <summary>The element clicked in the graph: its preview is shown beside the graph.</summary>
    public string? GraphNodeId { get => _graphNodeId; private set => SetProperty(ref _graphNodeId, value); }

    public bool HasGraphSelection => _graphNodeId != null;

    /// <summary>The whiteboard element of the selected node (drawn by the preview panel).</summary>
    public WbElement? GraphElement => WhiteboardOps.Find(Data, _graphNodeId);

    private string _graphTitle = "";
    public string GraphTitle { get => _graphTitle; private set => SetProperty(ref _graphTitle, value); }

    private string _graphInfo = "";
    public string GraphInfo { get => _graphInfo; private set => SetProperty(ref _graphInfo, value); }

    public void SelectGraphNode(string? id)
    {
        var node = id is null ? null : _graph.Find(id);
        var element = node is null ? null : WhiteboardOps.Find(Data, node.Id);
        if (node is null || element is null)
        {
            GraphNodeId = null;
            GraphTitle = "";
            GraphInfo = "";
        }
        else
        {
            GraphNodeId = node.Id;
            GraphTitle = node.Label;
            var linked = string.Join(", ", node.Neighbors.Select(n => n.Label));
            GraphInfo = node.Neighbors.Count == 0 ? $"{element.Kind}  ·  not connected" : $"{element.Kind}  ·  linked to: {linked}";
        }
        OnPropertyChanged(nameof(HasGraphSelection));
        OnPropertyChanged(nameof(GraphElement));
        RebuildGraphRefs();
    }

    // ------------------------------------------------------------------ references of the selected node

    /// <summary>The tasks and documents referenced by the element selected in the graph.</summary>
    public ObservableCollection<WbReference> GraphRefs { get; } = new();
    public bool HasGraphRefs => GraphRefs.Count > 0;
    public bool CanEditRefs => _board != null && _graphNodeId != null;

    public ICommand AddCardRefCommand { get; }
    public ICommand AddDocRefCommand { get; }

    private void RebuildGraphRefs()
    {
        GraphRefs.Clear();
        var element = GraphElement;
        if (element != null)
        {
            foreach (var cardId in element.CardRefs ?? new List<string>())
            {
                var id = cardId;
                var title = _board?.CardTitle(id);
                var archived = title is null ? _board?.ArchivedCardTitle(id) : null;
                GraphRefs.Add(new WbReference("task", title ?? (archived != null ? archived + "  (archived)" : "(task no longer on the board)"), title is null,
                    new RelayCommand(() => _board?.GoToCard(id)), new RelayCommand(() => RemoveCardRef(id))));
            }
            foreach (var path in element.DocRefs ?? new List<string>())
            {
                var target = path;
                var row = _board?.ReferenceRow(target);
                GraphRefs.Add(new WbReference("doc", row?.Title ?? target, row?.IsMissing ?? false,
                    new RelayCommand(() => _board?.GoToDocument(target)), new RelayCommand(() => RemoveDocRef(target)), target));
            }
        }
        OnPropertyChanged(nameof(HasGraphRefs));
        OnPropertyChanged(nameof(CanEditRefs));
    }

    /// <summary>The references changed: save, refresh the dot of the node and tell the board.</summary>
    private void CommitRefs()
    {
        Commit();
        if (_graphNodeId != null && _graph.Find(_graphNodeId) is { } node && WhiteboardOps.Find(Data, _graphNodeId) is { } element)
        {
            node.HasDot = element.HasRefs;
            _graphExtra.Add(element.Id); // stays in the graph even if the last reference was just removed
        }
        RebuildGraphRefs();
        GraphDecorationsChanged?.Invoke();
    }

    /// <summary>Stars or reference dots of the nodes changed: the graph control only needs to redraw.</summary>
    public event Action? GraphDecorationsChanged;

    private void AddCardRef()
    {
        var element = GraphElement;
        if (element is null || _board is null) return;
        var items = _board.CardChoices().Where(i => element.CardRefs?.Contains(i.Id) != true).ToList();
        var picker = new ItemPickerViewModel("Link a task", items, "Link", "There are no tasks to link on the board.");
        if (_dialogs.ShowDialog(picker) != true || picker.Selected is null) return;
        LinkCard(element.Id, picker.Selected.Id);
    }

    /// <summary>
    /// Links a task to an element. A task belongs to one element only: the link it had before
    /// (on this or on another whiteboard) is replaced.
    /// </summary>
    public bool LinkCard(string elementId, string cardId)
    {
        if (WhiteboardOps.Find(Data, elementId) is null) return false;
        // References are not part of the undo history: they also live in the tasks, the documents and the other boards.
        if (!WhiteboardLinks.LinkCard(Data, elementId, cardId)) return false;
        try { WhiteboardLinks.UnlinkCardElsewhere(_service, cardId, Data.Id); }
        catch { /* the other boards are fixed the next time the task is linked */ }
        CommitRefs();
        StatusText = "Task linked.";
        return true;
    }

    private void RemoveCardRef(string cardId)
    {
        if (!WhiteboardLinks.UnlinkCard(Data, cardId)) return;
        CommitRefs();
    }

    private void AddDocRef()
    {
        var element = GraphElement;
        if (element is null || _board is null) return;
        var items = _board.DocumentChoices()
            .Where(i => element.DocRefs?.Contains(i.Id, StringComparer.OrdinalIgnoreCase) != true).ToList();
        var picker = new ItemPickerViewModel("Link a document", items, "Link",
            "There are no documents to link yet: create them in the Documentation area.");
        if (_dialogs.ShowDialog(picker) != true || picker.Selected is null) return;
        LinkDoc(element.Id, picker.Selected.Id);
    }

    public bool LinkDoc(string elementId, string docPath)
    {
        if (!WhiteboardLinks.LinkDoc(Data, elementId, docPath)) return false;
        CommitRefs();
        StatusText = "Document linked.";
        return true;
    }

    private void RemoveDocRef(string docPath)
    {
        if (_graphNodeId is null) return;
        if (!WhiteboardLinks.UnlinkDoc(Data, _graphNodeId, docPath)) return;
        CommitRefs();
    }

    /// <summary>A document was renamed or moved: the references of this board follow it.</summary>
    public void RemapDocs(string oldPath, string newPath)
    {
        // The states kept for undo / redo too: an element that comes back must not point to the old name.
        RewriteHistory(data => WhiteboardLinks.RemapDocs(data, oldPath, newPath));
        if (!WhiteboardLinks.RemapDocs(Data, oldPath, newPath)) return;
        Save();
        RefreshGraphRefs();
    }

    /// <summary>A document or folder was deleted: the elements forget it.</summary>
    public void RemoveDocs(string path)
    {
        RewriteHistory(data => WhiteboardLinks.RemoveDocs(data, path));
        if (!WhiteboardLinks.RemoveDocs(Data, path)) return;
        Save();
        RefreshGraphRefs();
    }

    /// <summary>Tasks were deleted for good: the elements forget them.</summary>
    public void ForgetCards(ICollection<string> cardIds)
    {
        RewriteHistory(data => cardIds.Aggregate(false, (changed, id) => WhiteboardLinks.UnlinkCard(data, id) | changed));
        var changed = false;
        foreach (var id in cardIds) changed |= WhiteboardLinks.UnlinkCard(Data, id);
        if (!changed) return;
        Save();
        RefreshGraphRefs();
    }

    private void RewriteHistory(Func<WhiteboardData, bool> change)
    {
        foreach (var stack in new[] { _undo, _redo })
        {
            for (var i = 0; i < stack.Count; i++)
            {
                try
                {
                    var data = JsonFile.Deserialize<WhiteboardData>(stack[i]);
                    if (data != null && change(data)) stack[i] = JsonFile.Serialize(data);
                }
                catch { /* a state that cannot be read is left as it is */ }
            }
        }
    }

    /// <summary>
    /// Titles and dots of the graph follow what happened in the other areas (a task renamed or deleted,
    /// a document moved): called when the whiteboard is shown again.
    /// </summary>
    public void RefreshGraphRefs()
    {
        foreach (var node in _graph.Nodes)
            if (WhiteboardOps.Find(Data, node.Id) is { } element) node.HasDot = element.HasRefs;
        RebuildGraphRefs();
        GraphDecorationsChanged?.Invoke();
    }

    /// <summary>Shows an element of a whiteboard of the project, selected and centered. False when it no longer exists.</summary>
    public bool ShowElement(string boardId, string elementId)
    {
        if (Data.Id != boardId)
        {
            var board = Boards.FirstOrDefault(b => b.Id == boardId);
            if (board is null) return false;
            SelectedBoard = board;
        }
        if (WhiteboardOps.Find(Data, elementId) is null) return false;
        IsGraphMode = false;
        Tool = WbTool.Select;
        Select(new[] { elementId });
        ViewRequested?.Invoke("focus");
        return true;
    }

    // ------------------------------------------------------------------ favorites, disconnect, view in graph

    public ICommand ToggleFavoriteCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ViewInGraphCommand { get; }

    /// <summary>The star of the toolbar is lit when every selected element is a favorite.</summary>
    public bool IsFavorite => SelectedIds.Count > 0 && SelectedElements.All(e => e.Favorite);

    public bool CanViewInGraph => SelectedIds.Count == 1;
    public bool CanDisconnect => SelectedIds.Count > 0 && Data.Connectors.Any(c => SelectedIds.Contains(c.FromId) || SelectedIds.Contains(c.ToId));

    private void ToggleFavorite()
    {
        var elements = SelectedElements.ToList();
        if (elements.Count == 0) return;
        var value = !elements.All(e => e.Favorite);
        Snapshot();
        foreach (var element in elements) element.Favorite = value;
        Commit();
        StatusText = value ? "Marked as favorite: the star shows in the graph view." : "Favorite removed.";
    }

    /// <summary>Removes every connector attached to the selected elements.</summary>
    private void DisconnectSelection()
    {
        if (SelectedIds.Count == 0) return;
        Snapshot();
        var removed = WhiteboardOps.Disconnect(Data, SelectedIds.ToList());
        if (removed == 0)
        {
            DiscardSnapshot();
            StatusText = "Nothing to disconnect.";
            return;
        }
        StatusText = $"{removed} link(s) removed.";
        Commit();
    }

    /// <summary>Switches to the graph view with the node of the selected element selected.</summary>
    private void ViewInGraph()
    {
        if (SelectedIds.Count != 1) return;
        var id = SelectedIds.First();
        _graphExtra.Add(id);
        if (_isGraphMode) RebuildGraph();
        else IsGraphMode = true;
        SelectGraphNode(id);
    }

    /// <summary>Back to the drawing, with the element of the selected node selected and centered.</summary>
    private void ShowOnBoard()
    {
        var id = _graphNodeId;
        IsGraphMode = false;
        if (id is null || WhiteboardOps.Find(Data, id) is null) return;
        Tool = WbTool.Select;
        Select(new[] { id });
        ViewRequested?.Invoke("focus");
    }

    // ------------------------------------------------------------------ tools

    public IReadOnlyList<ToolOption> Tools { get; }

    private WbTool _tool = WbTool.Select;
    public WbTool Tool
    {
        get => _tool;
        set
        {
            if (!SetProperty(ref _tool, value)) return;
            foreach (var option in Tools) option.IsSelected = option.Tool == value;
            OnPropertyChanged(nameof(Hint));
            ToolChanged?.Invoke();
        }
    }

    /// <summary>Raised when another tool is picked (the canvas cancels what was in progress).</summary>
    public event Action? ToolChanged;

    private void SetTool(WbTool tool) => Tool = tool;

    public string Hint => Tools.First(t => t.Tool == _tool).Hint;

    private string _statusText = "";
    /// <summary>Feedback for the last action ("Already connected", "Saved", ...).</summary>
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _zoomText = "100%";
    public string ZoomText { get => _zoomText; set => SetProperty(ref _zoomText, value); }

    private bool _showGrid = true;
    public bool ShowGrid
    {
        get => _showGrid;
        set { if (SetProperty(ref _showGrid, value)) ViewRequested?.Invoke("grid"); }
    }

    // ------------------------------------------------------------------ style of new / selected elements

    public IReadOnlyList<string> Colors { get; } = new[]
    {
        "#000000", "#5F6B7A", "#9AA5B1", "#FFFFFF", "#E5484D", "#F76B15", "#F2A541", "#FFE08A",
        "#7CB518", "#30A46C", "#12A594", "#1F6F8B", "#0091FF", "#5B5BD6", "#8E4EC6", "#D6409F",
        "#FDECEC", "#FDF1DC", "#E6F4D7", "#DDF3EA", "#E3F0F4", "#E1E9FF", "#EFE5FA", "#16324F"
    };

    public IReadOnlyList<double> ThicknessOptions { get; } = new double[] { 1, 2, 3, 4, 6, 8, 12, 16 };
    public IReadOnlyList<double> ConnectorThicknessOptions { get; } = new double[] { 0.5, 1, 1.5, 2, 3, 4, 6 };
    public IReadOnlyList<double> FontSizeOptions { get; } = new double[] { 10, 12, 14, 16, 20, 24, 32, 48, 64 };

    private string? _strokeColor;
    /// <summary>Line / text color. null = automatic (theme color).</summary>
    public string? StrokeColor
    {
        get => _strokeColor;
        set
        {
            if (value != null && !Palette.IsValidHex(value)) return;
            if (!SetProperty(ref _strokeColor, value)) return;
            OnPropertyChanged(nameof(IsStrokeAuto));
            ApplyStyle(e => e.Stroke = value, c => c.Color = value);
        }
    }

    public bool IsStrokeAuto => _strokeColor is null;

    private string? _fillColor;
    /// <summary>Fill color. null = no fill.</summary>
    public string? FillColor
    {
        get => _fillColor;
        set
        {
            if (value != null && !Palette.IsValidHex(value)) return;
            if (!SetProperty(ref _fillColor, value)) return;
            OnPropertyChanged(nameof(HasFill));
            ApplyStyle(e => { if (e.Kind != WbKind.Image) e.Fill = value; }, null);
        }
    }

    public bool HasFill => _fillColor != null;

    private double _thickness = 2;
    public double Thickness
    {
        get => _thickness;
        set { if (SetProperty(ref _thickness, value)) ApplyStyle(e => { if (e.IsDrawing) e.StrokeWidth = value; }, null); }
    }

    private double _fontSize = 16;
    public double FontSize
    {
        get => _fontSize;
        set { if (SetProperty(ref _fontSize, value)) ApplyStyle(e => { if (e.Kind == WbKind.Text) e.FontSize = value; }, null); }
    }

    private double _connectorThickness = 1;
    public double ConnectorThickness
    {
        get => _connectorThickness;
        set { if (SetProperty(ref _connectorThickness, value)) ApplyStyle(null, c => c.Thickness = value); }
    }

    private bool _connectorArrow;
    public bool ConnectorArrow
    {
        get => _connectorArrow;
        set { if (SetProperty(ref _connectorArrow, value)) ApplyStyle(null, c => c.Arrow = value); }
    }

    public ICommand StrokeAutoCommand { get; }
    public ICommand FillNoneCommand { get; }

    /// <summary>A style control changed: update the selected elements / connector (one undo step).</summary>
    private void ApplyStyle(Action<WbElement>? toElement, Action<WbConnector>? toConnector)
    {
        if (_syncingStyle) return;
        var elements = toElement is null ? new List<WbElement>() : SelectedElements.ToList();
        var connector = toConnector is null ? null : SelectedConnector;
        if (elements.Count == 0 && connector is null) return;

        Snapshot();
        foreach (var element in elements) ApplyDeep(element, toElement!);
        if (connector != null) toConnector!(connector);
        Commit();
    }

    private static void ApplyDeep(WbElement element, Action<WbElement> action)
    {
        action(element);
        if (element.Children != null)
            foreach (var child in element.Children) ApplyDeep(child, action);
    }

    /// <summary>Shows in the style controls the style of what is selected.</summary>
    private void SyncStyleFromSelection()
    {
        _syncingStyle = true;
        try
        {
            var element = SelectedElements.FirstOrDefault(e => e.Kind != WbKind.Group);
            if (element != null)
            {
                StrokeColor = element.Stroke;
                if (element.Kind != WbKind.Image) FillColor = element.Fill;
                if (element.IsDrawing && element.StrokeWidth > 0) Thickness = element.StrokeWidth;
                if (element.Kind == WbKind.Text && element.FontSize > 0) FontSize = element.FontSize;
            }
            var connector = SelectedConnector;
            if (connector != null)
            {
                ConnectorThickness = connector.Thickness;
                ConnectorArrow = connector.Arrow;
                StrokeColor = connector.Color;
            }
        }
        finally
        {
            _syncingStyle = false;
        }
    }

    // ------------------------------------------------------------------ selection

    public HashSet<string> SelectedIds { get; } = new();

    public string? SelectedConnectorId { get; private set; }

    public IEnumerable<WbElement> SelectedElements => Data.Elements.Where(e => SelectedIds.Contains(e.Id));

    public WbConnector? SelectedConnector =>
        SelectedConnectorId is null ? null : Data.Connectors.FirstOrDefault(c => c.Id == SelectedConnectorId);

    public bool HasSelection => SelectedIds.Count > 0 || SelectedConnectorId != null;
    public bool HasElementSelection => SelectedIds.Count > 0;
    public bool CanGroup => SelectedIds.Count >= 2;
    public bool CanUngroup => SelectedElements.Any(e => e.Kind == WbKind.Group);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string CountText => $"{Data.Elements.Count} elements · {Data.Connectors.Count} links";

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasElementSelection));
        OnPropertyChanged(nameof(CanGroup));
        OnPropertyChanged(nameof(CanUngroup));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(CanViewInGraph));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    private void OnSelectionChanged()
    {
        SyncStyleFromSelection();
        RaiseStateChanged();
        SelectionChanged?.Invoke();
    }

    public void Select(IEnumerable<string> ids)
    {
        SelectedIds.Clear();
        foreach (var id in ids) SelectedIds.Add(id);
        SelectedConnectorId = null;
        OnSelectionChanged();
    }

    public void ToggleSelect(string id)
    {
        if (!SelectedIds.Remove(id)) SelectedIds.Add(id);
        SelectedConnectorId = null;
        OnSelectionChanged();
    }

    public void SelectConnector(string id)
    {
        SelectedIds.Clear();
        SelectedConnectorId = id;
        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        if (!HasSelection) return;
        SelectedIds.Clear();
        SelectedConnectorId = null;
        OnSelectionChanged();
    }

    private void SelectAll() => Select(Data.Elements.Select(e => e.Id).ToList());

    // ------------------------------------------------------------------ undo / save

    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    /// <summary>Remembers the current board so the next change can be undone. Call it BEFORE modifying <see cref="Data"/>.</summary>
    public void Snapshot()
    {
        _undo.Add(JsonFile.Serialize(Data));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Drops the last snapshot (the operation was cancelled or changed nothing).</summary>
    public void DiscardSnapshot()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
        RaiseStateChanged();
    }

    /// <summary>Call it AFTER modifying <see cref="Data"/>: auto-saves and refreshes the canvas.</summary>
    public void Commit()
    {
        // Selected things may not exist any more.
        SelectedIds.RemoveWhere(id => Data.Elements.All(e => e.Id != id));
        if (SelectedConnectorId != null && Data.Connectors.All(c => c.Id != SelectedConnectorId)) SelectedConnectorId = null;

        Save();
        RaiseStateChanged();
        DocumentChanged?.Invoke();
        _board?.OnWhiteboardLinksChanged();
    }

    public void Save()
    {
        try
        {
            _service.Save(Data);
        }
        catch (Exception ex)
        {
            StatusText = "SAVE FAILED: " + ex.Message;
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(JsonFile.Serialize(Data));
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        AfterHistoryJump();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(JsonFile.Serialize(Data));
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        AfterHistoryJump();
    }

    private void Restore(string json)
    {
        var restored = JsonFile.Deserialize<WhiteboardData>(json);
        if (restored is null) return;
        WhiteboardLinks.CarryReferences(Data, restored, () => WhiteboardLinks.CardsLinkedOnDisk(_service, Data.Id));
        Data = restored;
    }

    private void AfterHistoryJump()
    {
        SelectedIds.Clear();
        SelectedConnectorId = null;
        Save();
        RaiseStateChanged();
        DocumentChanged?.Invoke();
        SelectionChanged?.Invoke();
        if (_isGraphMode) RebuildGraph();
        _board?.OnWhiteboardLinksChanged();
    }

    // ------------------------------------------------------------------ editing commands

    public ICommand DeleteCommand { get; }
    public ICommand GroupCommand { get; }
    public ICommand UngroupCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand FrontCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand AddImageCommand { get; }

    /// <summary>Gives a new element the current style and adds it on top (one undo step).</summary>
    public void AddElement(WbElement element, bool select = true)
    {
        Snapshot();
        Data.Elements.Add(element);
        if (select)
        {
            SelectedIds.Clear();
            SelectedIds.Add(element.Id);
            SelectedConnectorId = null;
        }
        Commit();
        if (select)
        {
            RaiseStateChanged();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Applies the current stroke / fill / thickness to an element that is about to be added.</summary>
    public void StyleNew(WbElement element)
    {
        element.Stroke = StrokeColor;
        switch (element.Kind)
        {
            case WbKind.Text:
                element.FontSize = FontSize;
                element.StrokeWidth = 0;
                break;
            case WbKind.Image:
                element.Stroke = null;
                element.StrokeWidth = 0;
                break;
            case WbKind.Rectangle or WbKind.Ellipse or WbKind.Triangle or WbKind.Polygon:
                element.StrokeWidth = Thickness;
                element.Fill = FillColor;
                break;
            default:
                element.StrokeWidth = Thickness; // lines and freehand strokes start without fill
                break;
        }
    }

    /// <summary>Creates a connector between two elements, unless they are already linked.</summary>
    public bool TryConnect(string fromId, string toId)
    {
        if (fromId == toId) return false;
        if (WhiteboardOps.HasConnector(Data, fromId, toId))
        {
            StatusText = "These two elements are already connected.";
            return false;
        }
        Snapshot();
        var connector = WhiteboardOps.AddConnector(Data, fromId, toId, ConnectorThickness, ConnectorArrow);
        if (connector is null)
        {
            DiscardSnapshot();
            return false;
        }
        StatusText = "Connected.";
        SelectedIds.Clear();
        SelectedConnectorId = connector.Id;
        Commit();
        SelectionChanged?.Invoke();
        return true;
    }

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        Snapshot();
        if (SelectedConnectorId != null) Data.Connectors.RemoveAll(c => c.Id == SelectedConnectorId);
        WhiteboardOps.Delete(Data, SelectedIds.ToList());
        SelectedIds.Clear();
        SelectedConnectorId = null;
        Commit();
        SelectionChanged?.Invoke();
    }

    /// <summary>"Merge": the selected elements become one element.</summary>
    private void GroupSelection()
    {
        if (!CanGroup) return;
        Snapshot();
        var group = WhiteboardOps.Group(Data, SelectedIds.ToList());
        if (group is null)
        {
            DiscardSnapshot();
            return;
        }
        SelectedIds.Clear();
        SelectedIds.Add(group.Id);
        StatusText = $"Merged {group.Children!.Count} elements into one.";
        Commit();
        OnSelectionChanged();
    }

    private void UngroupSelection()
    {
        var groups = SelectedElements.Where(e => e.Kind == WbKind.Group).Select(e => e.Id).ToList();
        if (groups.Count == 0) return;
        Snapshot();
        var released = new List<string>();
        foreach (var id in groups)
        {
            SelectedIds.Remove(id);
            released.AddRange(WhiteboardOps.Ungroup(Data, id).Select(e => e.Id));
        }
        foreach (var id in released) SelectedIds.Add(id);
        Commit();
        OnSelectionChanged();
    }

    private void DuplicateSelection()
    {
        if (SelectedIds.Count == 0) return;
        Snapshot();
        var copies = WhiteboardOps.Duplicate(Data, SelectedIds.ToList());
        SelectedIds.Clear();
        foreach (var copy in copies) SelectedIds.Add(copy.Id);
        Commit();
        OnSelectionChanged();
    }

    private void Reorder(bool front)
    {
        if (SelectedIds.Count == 0) return;
        Snapshot();
        if (front) WhiteboardOps.BringToFront(Data, SelectedIds.ToList());
        else WhiteboardOps.SendToBack(Data, SelectedIds.ToList());
        Commit();
    }

    // ------------------------------------------------------------------ copy / paste of elements

    private class ClipboardPayload
    {
        public List<WbElement> Elements { get; set; } = new();
        public List<WbConnector> Connectors { get; set; } = new();
    }

    /// <summary>JSON of the selected elements (and the links between them), or null when nothing is selected.</summary>
    public string? CopySelection()
    {
        var elements = SelectedElements.ToList();
        if (elements.Count == 0) return null;
        var ids = elements.Select(e => e.Id).ToHashSet();
        return JsonFile.Serialize(new ClipboardPayload
        {
            Elements = elements,
            Connectors = Data.Connectors.Where(c => ids.Contains(c.FromId) && ids.Contains(c.ToId)).ToList()
        });
    }

    /// <summary>Adds the elements produced by <see cref="CopySelection"/>, centered on <paramref name="center"/>.</summary>
    public bool Paste(string json, WbPoint center)
    {
        ClipboardPayload? payload;
        try { payload = JsonFile.Deserialize<ClipboardPayload>(json); }
        catch { return false; }
        if (payload is null || payload.Elements.Count == 0) return false;

        var bounds = WhiteboardOps.Bounds(payload.Elements);
        var dx = center.X - (bounds.Left + bounds.Width / 2);
        var dy = center.Y - (bounds.Top + bounds.Height / 2);

        Snapshot();
        var map = new Dictionary<string, string>();
        SelectedIds.Clear();
        SelectedConnectorId = null;
        HashSet<string>? elsewhere = null;
        foreach (var source in payload.Elements)
        {
            var copy = WhiteboardOps.CloneWithNewIds(source);
            copy.X += dx;
            copy.Y += dy;
            map[source.Id] = copy.Id;
            Data.Elements.Add(copy);
            SelectedIds.Add(copy.Id);

            // A copy never takes the tasks of the original, but an element that was cut (the only way to
            // move it to another board) keeps them: a task that no element holds any more goes to the pasted one.
            foreach (var cardId in source.CardRefs ?? new List<string>())
            {
                if (_board != null && _board.CardTitle(cardId) is null && !_board.IsArchivedCard(cardId)) continue;
                try { elsewhere ??= WhiteboardLinks.CardsLinkedOnDisk(_service, Data.Id); }
                catch { elsewhere = new HashSet<string>(); }
                if (!elsewhere.Contains(cardId)) WhiteboardLinks.LinkFreeCard(Data, copy, cardId);
            }
        }
        foreach (var connector in payload.Connectors)
        {
            if (map.TryGetValue(connector.FromId, out var from) && map.TryGetValue(connector.ToId, out var to))
                Data.Connectors.Add(new WbConnector
                {
                    FromId = from, ToId = to, Thickness = connector.Thickness, Color = connector.Color, Arrow = connector.Arrow
                });
        }
        Commit();
        OnSelectionChanged();
        return true;
    }
}
