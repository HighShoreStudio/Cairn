using System.Collections.ObjectModel;
using System.Windows.Input;
using HighshoreCairn.Models;
using HighshoreCairn.Services;

namespace HighshoreCairn.ViewModels;

/// <summary>A folder or a file in the documentation tree.</summary>
public class DocNodeViewModel : ObservableObject
{
    public DocNodeViewModel(DocNode node, DocNodeViewModel? parent)
    {
        Node = node;
        Parent = parent;
        foreach (var child in node.Children) Children.Add(new DocNodeViewModel(child, this));
    }

    public DocNode Node { get; }
    public DocNodeViewModel? Parent { get; }
    public ObservableCollection<DocNodeViewModel> Children { get; } = new();

    public string Path => Node.Path;
    /// <summary>Name shown in the tree: markdown documents without ".md", everything else as it is.</summary>
    public string Name =>
        Node.Kind == DocKind.Text && Node.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? Node.Name[..^3]
        : Node.Kind == DocKind.Rich ? Node.Name[..^DocsService.RichExtension.Length]
        : Node.Name;
    public bool IsFolder => Node.IsFolder;
    public bool IsLink => Node.IsLink;
    public bool IsBroken => Node.IsBroken;
    public bool IsText => Node.Kind == DocKind.Text;
    public bool IsRich => Node.Kind == DocKind.Rich;
    /// <summary>A document edited in the app (markdown, plain text or rich text).</summary>
    public bool IsDocument => Node.IsDocument;

    /// <summary>Which picture the tree shows: "folder", "markdown", "text", "rich", "image", "link" or "file".</summary>
    public string IconKind => Node.Kind switch
    {
        DocKind.Folder => "folder",
        _ when Node.IsLink => "link",
        DocKind.Text => Node.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text" : "markdown",
        DocKind.Rich => "rich",
        DocKind.Image => "image",
        _ => "file"
    };

    /// <summary>Glyph of the Windows icon font for the kind of item.</summary>
    public string Icon => Node.Kind switch
    {
        DocKind.Folder => "",
        DocKind.Text => "",
        DocKind.Image => "",
        _ => ""
    };

    public string ToolTip
    {
        get
        {
            if (Node.IsFolder) return Node.Path;
            var text = Node.Path;
            if (Node.IsLink)
                text = (Node.IsBroken ? "Broken link (the original file is missing):\n" : "Link to:\n") + (Node.LinkTarget ?? "?");
            return text + (Node.Modified == default ? "" : $"\nModified {Node.Modified:yyyy-MM-dd HH:mm}");
        }
    }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private bool _isDropTarget;
    public bool IsDropTarget { get => _isDropTarget; set => SetProperty(ref _isDropTarget, value); }

    public IEnumerable<DocNodeViewModel> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants()) yield return d;
        }
    }
}

/// <summary>A document that is open in the Documentation area (in a tab or in a floating window).</summary>
public class DocumentViewModel : ObservableObject
{
    private readonly DocsViewModel _owner;
    private DateTime _stamp;
    private bool _usesCrLf;
    private System.Text.Encoding? _encoding; // the encoding of the file: it is written back the same way
    private bool _loading;
    private bool _infoStale = true;
    private bool _statsStale = true;
    private string _stats = "";

    public DocumentViewModel(DocsViewModel owner, DocNode node, bool preview)
    {
        _owner = owner;
        Node = node;
        _isPreview = preview && node.Kind == DocKind.Text;   // only markdown / text documents have the two views
        CloseCommand = new RelayCommand(() => _owner.Close(this));
        SaveCommand = new RelayCommand(() => { Flush(); Save(); });
        FloatCommand = new RelayCommand(() => _owner.OpenInWindow(this));
        DockCommand = new RelayCommand(() => _owner.OpenAsTab(this));
        ShowPreviewCommand = new RelayCommand(() => IsPreview = true);
        ShowMarkdownCommand = new RelayCommand(() => IsPreview = false);
        ExportHtmlCommand = new RelayCommand(() => _owner.ExportHtml(this));
        OpenExternalCommand = new RelayCommand(() => _owner.OpenExternal(Path));
        ConvertCommand = new RelayCommand(() => _owner.Convert(this));
        Load(initial: true);
        GoToHeadingCommand = new RelayCommand<MdOutlineItem>(item =>
        {
            if (item is null) return;
            // The editor may not exist yet (document just opened): it picks the request up when it loads.
            if (ScrollRequested is null) PendingScroll = item;
            else ScrollRequested(item);
        });
    }

    public DocsViewModel Owner => _owner;
    public DocNode Node { get; private set; }

    public string Path => Node.Path;
    public string Name => Node.Name;
    public string Title => Node.Title;
    public bool IsLink => Node.IsLink;
    public bool IsText => Node.Kind == DocKind.Text;
    /// <summary>A rich text (.rtf) document: <see cref="Text"/> holds the RTF source, edited by the rich text view.</summary>
    public bool IsRich => Node.Kind == DocKind.Rich;
    /// <summary>The document has a toolbar and is saved by the app.</summary>
    public bool IsEditable => IsText || IsRich;
    public bool IsImage => Node.Kind == DocKind.Image;

    // What the editor shows: the markdown source, the formatted markdown, or the rich text.
    public bool ShowSource => IsText && !_isPreview;
    public bool ShowFormatted => IsText && _isPreview;
    public bool ShowRich => IsRich;

    /// <summary>Markdown and rich text documents stored in the project can be converted into each other.</summary>
    public bool CanConvert => IsEditable && !IsLink && !HasError;
    public string ConvertText => IsRich ? "Convert to .MD" : "Convert to .RTF";
    /// <summary>For pictures: the file to show.</summary>
    public string? ImageFile { get; private set; }

    public string Header => Title + (IsDirty ? " •" : "");

    private bool _isActive;
    /// <summary>This is the tab shown in the workspace.</summary>
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public string ToolTip => (IsLink ? "Linked file: " + Node.LinkTarget : _owner.Service.FullPath(Path)) + (IsDirty ? "\n(unsaved changes)" : "");

    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand FloatCommand { get; }
    public ICommand DockCommand { get; }
    public ICommand ShowPreviewCommand { get; }
    public ICommand ShowMarkdownCommand { get; }
    public ICommand ExportHtmlCommand { get; }
    public ICommand OpenExternalCommand { get; }
    public ICommand ConvertCommand { get; }
    public ICommand GoToHeadingCommand { get; }

    /// <summary>The view must write pending edits of the formatted view into <see cref="Text"/>.</summary>
    public event Action? FlushRequested;
    /// <summary>The view must scroll to a heading (Index >= 0) or to a line of the source (Index = -1).</summary>
    public event Action<MdOutlineItem>? ScrollRequested;
    /// <summary>A scroll request made before the editor of this document was created.</summary>
    public MdOutlineItem? PendingScroll { get; set; }
    /// <summary>The text was replaced from outside the editor (reload from disk).</summary>
    public event Action? Reloaded;

    private string _text = "";
    /// <summary>The markdown source of the document.</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (!SetProperty(ref _text, value)) return;
            if (!IsRich) OnPropertyChanged(nameof(SourceText));
            if (_loading) return;
            IsDirty = true;
            LastEditUtc = DateTime.UtcNow;
            _infoStale = true;
            _statsStale = true; // recomputed a moment later (see RefreshStats), not at every key
        }
    }

    /// <summary>
    /// What the source (Markdown) view edits: the text of a markdown document. Empty for every other kind,
    /// so that the RTF codes of a rich text document never end up in a text box.
    /// </summary>
    public string SourceText
    {
        get => IsText ? _text : "";
        set { if (IsText) Text = value; }
    }

    public DateTime LastEditUtc { get; private set; }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    private bool _isPreview;
    /// <summary>true = formatted view with the toolbar, false = markdown source.</summary>
    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            if (_isPreview == value || !IsText) return;
            if (!value) Flush(); // bring the edits made in the formatted view into the markdown text
            else RefreshPreviewLock(); // the text may have changed in the Markdown view
            _isPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMarkdown));
            OnPropertyChanged(nameof(ShowPreviewLock));
            OnPropertyChanged(nameof(ShowSource));
            OnPropertyChanged(nameof(ShowFormatted));
        }
    }

    public bool IsMarkdown => !_isPreview;

    private string? _error;
    /// <summary>Set when the file could not be read (for example a broken link).</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(CanConvert));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ------------------------------------------------------------------ info panel

    public ObservableCollection<MdOutlineItem> Outline { get; } = new();
    public ObservableCollection<DocNode> Backlinks { get; } = new();
    public bool HasOutline => Outline.Count > 0;
    public bool HasBacklinks => Backlinks.Count > 0;

    /// <summary>Words, characters and reading time of the document.</summary>
    public string StatsText
    {
        get => _stats;
    }

    /// <summary>Recounts the words when the text changed since the last count.</summary>
    public void RefreshStats()
    {
        if (!_statsStale) return;
        _statsStale = false;
        if (IsRich)
        {
            var plain = RtfText.ToPlain(_text);
            var words = MdDoc.CountWords(plain);
            var minutes = Math.Max(1, (int)Math.Round(words / 200.0));
            _stats = $"{words} words  ·  {plain.Count(c => c != '\n')} characters  ·  ~{minutes} min read  ·  rich text";
        }
        else if (!IsText) _stats = IsImage ? "Picture" : "";
        else
        {
            var words = MdDoc.CountWords(MdDoc.PlainText(MdDoc.Parse(_text)));
            var minutes = Math.Max(1, (int)Math.Round(words / 200.0));
            _stats = $"{words} words  ·  {_text.Length} characters  ·  ~{minutes} min read";
        }
        OnPropertyChanged(nameof(StatsText));
    }

    private string? _previewLock;
    /// <summary>
    /// Not null when the document uses markdown that the formatted view cannot keep (HTML, reference links,
    /// footnotes): the Preview is then read-only and the text is edited in the Markdown view.
    /// </summary>
    public string? PreviewLock
    {
        get => _previewLock;
        private set
        {
            if (!SetProperty(ref _previewLock, value)) return;
            OnPropertyChanged(nameof(IsPreviewLocked));
            OnPropertyChanged(nameof(PreviewLockText));
            OnPropertyChanged(nameof(ShowPreviewLock));
        }
    }

    public bool IsPreviewLocked => _previewLock != null;
    /// <summary>The notice about the read-only Preview is on screen.</summary>
    public bool ShowPreviewLock => _isPreview && _previewLock != null;
    public string PreviewLockText => _previewLock is null ? "" :
        $"This document uses {_previewLock}: the Preview is read-only for it, so nothing gets rewritten. Edit it in the Markdown view.";

    /// <summary>Checks again whether the Preview can edit this document (after the text changed in the Markdown view).</summary>
    public void RefreshPreviewLock() => PreviewLock = IsText ? MdDoc.UnsupportedSyntax(_text) : null;

    /// <summary>Recomputes outline and backlinks (only when the text changed since the last time).</summary>
    public void RefreshInfo(bool force = false)
    {
        if (!_infoStale && !force) return;
        _infoStale = false;
        Outline.Clear();
        if (IsText)
            foreach (var item in MdDoc.Outline(_text)) Outline.Add(item);
        Backlinks.Clear();
        try
        {
            foreach (var doc in _owner.Service.Backlinks(Path)) Backlinks.Add(doc);
        }
        catch { /* backlinks are optional */ }
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(HasBacklinks));
    }

    // ------------------------------------------------------------------ load / save

    public void Flush() => FlushRequested?.Invoke();

    /// <summary>Reads the file. Returns false when it could not be read.</summary>
    private bool Load(bool initial)
    {
        string? raw = null;
        try
        {
            if (IsEditable) raw = _owner.Service.Read(Path, out _encoding);
            else if (IsImage) ImageFile = _owner.Service.ContentPath(Path);
        }
        catch (Exception ex)
        {
            // A reload that fails (file busy for a moment) keeps what is on screen: it is tried again later.
            if (initial) Error = ex.Message;
            return false;
        }

        _loading = true;
        try
        {
            Error = null;
            if (raw != null && IsRich)
            {
                _usesCrLf = false;
                Text = raw; // the RTF source is kept exactly as it is
            }
            else if (raw != null)
            {
                _usesCrLf = raw.Contains("\r\n");
                Text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            }
            _stamp = _owner.Service.LastWrite(Path);
        }
        finally
        {
            _loading = false;
        }
        IsDirty = false;
        _infoStale = true;
        _statsStale = true;
        RefreshStats();
        RefreshPreviewLock();
        if (IsPreviewLocked && initial) _isPreview = false; // such documents open in the Markdown view
        return true;
    }

    /// <summary>Re-reads the file (after a rename, or when another program changed it).</summary>
    public void Reload()
    {
        if (Load(initial: HasError)) Reloaded?.Invoke();
    }

    /// <summary>True when the file on disk is newer than what was loaded and there are no local edits.</summary>
    public bool ChangedOnDisk => !IsDirty && !HasError && _owner.Service.LastWrite(Path) != _stamp;

    /// <summary>Writes the document when it has changes. Returns false when they could not be saved.</summary>
    public bool Save()
    {
        if (!IsDirty || !IsEditable) return true;
        if (HasError) return false; // the file was never read: writing would replace it with nothing useful
        try
        {
            var text = _text;
            if (IsText && text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            _encoding = _owner.Service.Write(Path, _usesCrLf ? text.Replace("\n", "\r\n") : text, _encoding);
            _stamp = _owner.Service.LastWrite(Path);
            IsDirty = false;
            RefreshStats();
            RefreshPreviewLock();
            return true;
        }
        catch (Exception ex)
        {
            _owner.ReportSaveError(this, ex);
            return false;
        }
    }

    /// <summary>Points the open document to its new location after a rename or a move.</summary>
    public void Retarget(DocNode node)
    {
        Node = node;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>A small floating window of the Documentation workspace, holding one document.</summary>
public class DocWindowViewModel : ObservableObject
{
    private readonly DocsViewModel _owner;

    public DocWindowViewModel(DocsViewModel owner, DocumentViewModel document)
    {
        _owner = owner;
        Document = document;
        CloseCommand = new RelayCommand(() => _owner.Close(Document));
        DockCommand = new RelayCommand(() => _owner.OpenAsTab(Document));
        ActivateCommand = new RelayCommand(() => _owner.BringToFront(this));
    }

    public DocumentViewModel Document { get; }
    public ICommand CloseCommand { get; }
    /// <summary>Enlarges the window to the whole workspace (it becomes a tab).</summary>
    public ICommand DockCommand { get; }
    public ICommand ActivateCommand { get; }

    private double _x;
    public double X { get => _x; set => SetProperty(ref _x, value); }

    private double _y;
    public double Y { get => _y; set => SetProperty(ref _y, value); }

    private double _width = 480;
    public double Width { get => _width; set => SetProperty(ref _width, Math.Max(280, value)); }

    private double _height = 420;
    public double Height { get => _height; set => SetProperty(ref _height, Math.Max(180, value)); }

    private int _zIndex;
    public int ZIndex { get => _zIndex; set => SetProperty(ref _zIndex, value); }
}

/// <summary>"New document" dialog: name + starting template.</summary>
public class NewDocumentViewModel : DialogViewModel
{
    public NewDocumentViewModel(string folder, string suggestedName = "", bool rich = false)
    {
        IsRich = rich;
        FolderLabel = folder.Length == 0 ? "docs" : "docs/" + folder;
        _name = suggestedName;
        _template = Templates[0];
        CreateCommand = new RelayCommand(Create);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    /// <summary>A rich text (.rtf) document instead of a markdown one: it starts empty, without a template.</summary>
    public bool IsRich { get; }
    public bool ShowTemplates => !IsRich;
    public string Heading => IsRich ? "New rich text document" : "New document";
    public string FolderLabel { get; }
    public IReadOnlyList<DocTemplate> Templates => DocTemplate.All;

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private DocTemplate _template;
    public DocTemplate Template { get => _template; set => SetProperty(ref _template, value ?? Templates[0]); }

    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    private void Create()
    {
        Error = DocsService.ValidateName(Name);
        if (Error is null) Close(true);
    }
}

/// <summary>Dialog that picks one document of the project (used to insert a link).</summary>
public class DocPickerViewModel : DialogViewModel
{
    private readonly List<DocNode> _all;

    public DocPickerViewModel(IEnumerable<DocNode> documents, string title = "Link to a document")
    {
        Title = title;
        _all = documents.OrderBy(d => d.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        OkCommand = new RelayCommand(() => { if (Selected != null) Close(true); });
        CancelCommand = new RelayCommand(() => Close(false));
        Apply();
    }

    public string Title { get; }
    public ObservableCollection<DocNode> Items { get; } = new();

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) Apply(); }
    }

    private DocNode? _selected;
    public DocNode? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }

    private void Apply()
    {
        Items.Clear();
        foreach (var doc in _all.Where(d => d.Path.Contains(_filter.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            Items.Add(doc);
        Selected = Items.FirstOrDefault();
    }
}

/// <summary>
/// The Documentation area of a project: the tree of the docs folder, the open documents (tabs and
/// floating windows), search, and the graph of the references between documents.
/// </summary>
public class DocsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Func<DocsSettings> _settings;
    private readonly IDocConverter? _converter;
    private int _zCounter;

    public DocsViewModel(string projectFolder, IDialogService dialogs, Func<DocsSettings>? settings = null, IDocConverter? converter = null)
    {
        _dialogs = dialogs;
        _settings = settings ?? (() => new DocsSettings());
        _converter = converter;
        Service = new DocsService(projectFolder);

        NewDocumentCommand = new RelayCommand(() => NewDocument());
        NewRichDocumentCommand = new RelayCommand(() => NewDocument(rich: true));
        NewFolderCommand = new RelayCommand(NewFolder);
        ImportCommand = new RelayCommand(ImportWithDialog);
        RenameCommand = new RelayCommand<DocNodeViewModel>(node => Rename(node ?? SelectedNode));
        DeleteCommand = new RelayCommand<DocNodeViewModel>(node => Delete(node ?? SelectedNode));
        OpenCommand = new RelayCommand<DocNodeViewModel>(node => OpenNode(node ?? SelectedNode, false));
        OpenInWindowCommand = new RelayCommand<DocNodeViewModel>(node => OpenNode(node ?? SelectedNode, true));
        OpenExternalCommand = new RelayCommand<DocNodeViewModel>(node => { if ((node ?? SelectedNode) is { } n) OpenExternal(n.Path); });
        RevealCommand = new RelayCommand<DocNodeViewModel>(Reveal);
        RefreshCommand = new RelayCommand(() => { CheckExternalChanges(); Refresh(); });
        ToggleGraphCommand = new RelayCommand(() => IsGraphVisible = !IsGraphVisible);
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
        ToggleTreeCommand = new RelayCommand(() => IsTreeVisible = !IsTreeVisible);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        OpenHitCommand = new RelayCommand<DocSearchHit>(OpenHit);
        OpenBacklinkCommand = new RelayCommand<DocNode>(node => { if (node != null) Open(node.Path); });
        GraphOpenCommand = new RelayCommand(() => OpenGraphSelection(false));
        GraphOpenInWindowCommand = new RelayCommand(() => OpenGraphSelection(true));
        GraphCreateCommand = new RelayCommand(CreateGraphGhost);
        SaveAllCommand = new RelayCommand(() => SaveAll());

        Refresh();
    }

    public DocsService Service { get; }
    public IDialogService Dialogs => _dialogs;
    public DocsSettings Settings => _settings();

    public ICommand NewDocumentCommand { get; }
    public ICommand NewRichDocumentCommand { get; }
    public ICommand NewFolderCommand { get; }

    /// <summary>
    /// A document or a folder got a new path (rename, move, conversion): old path, new path.
    /// The project uses it to keep the references of tasks and whiteboard elements valid.
    /// </summary>
    public event Action<string, string>? PathChanged;

    /// <summary>A document or a folder was deleted (moved to docs/.trash): what pointed to it should forget it.</summary>
    public event Action<string>? PathRemoved;
    public ICommand ImportCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenInWindowCommand { get; }
    public ICommand OpenExternalCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleGraphCommand { get; }
    public ICommand ToggleInfoCommand { get; }
    public ICommand ToggleTreeCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenHitCommand { get; }
    public ICommand OpenBacklinkCommand { get; }
    public ICommand GraphOpenCommand { get; }
    public ICommand GraphOpenInWindowCommand { get; }
    public ICommand GraphCreateCommand { get; }
    public ICommand SaveAllCommand { get; }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ------------------------------------------------------------------ tree

    private bool _isTreeVisible = true;
    /// <summary>False hides the tree, so the open document takes the whole area (like the whiteboard).</summary>
    public bool IsTreeVisible { get => _isTreeVisible; set => SetProperty(ref _isTreeVisible, value); }

    /// <summary>The items at the top level of the docs folder.</summary>
    public ObservableCollection<DocNodeViewModel> Nodes { get; } = new();

    public bool IsEmpty => Nodes.Count == 0;
    public bool HasNodes => Nodes.Count > 0;

    private DocNodeViewModel? _selectedNode;
    public DocNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedNode != null;

    private IEnumerable<DocNodeViewModel> AllNodes() => Nodes.SelectMany(n => new[] { n }.Concat(n.Descendants()));

    public DocNodeViewModel? FindNode(string path) =>
        AllNodes().FirstOrDefault(n => n.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-reads the docs folder, keeping the expanded folders and the selection.</summary>
    public void Refresh(string? select = null)
    {
        var expanded = AllNodes().Where(n => n.IsExpanded).Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        select ??= SelectedNode?.Path;

        Nodes.Clear();
        DocNode tree;
        try
        {
            tree = Service.Tree();
        }
        catch (Exception ex)
        {
            StatusText = "The documentation folder could not be read: " + ex.Message;
            tree = new DocNode { Kind = DocKind.Folder };
        }
        foreach (var child in tree.Children) Nodes.Add(new DocNodeViewModel(child, null));

        DocNodeViewModel? selected = null;
        foreach (var node in AllNodes())
        {
            if (expanded.Contains(node.Path)) node.IsExpanded = true;
            if (select != null && node.Path.Equals(select, StringComparison.OrdinalIgnoreCase)) selected = node;
        }
        // Make the selected item visible.
        for (var parent = selected?.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        if (selected != null) selected.IsSelected = true;
        SelectedNode = selected;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNodes));
        var files = AllNodes().Count(n => !n.IsFolder);
        StatusText = files == 0 ? "No documents yet" : $"{files} document{(files == 1 ? "" : "s")}";
        if (!string.IsNullOrWhiteSpace(_searchText)) RunSearch();
        if (_isGraphVisible) RebuildGraph();
    }

    /// <summary>The folder where new items go: the selected folder, or the folder of the selected file.</summary>
    public string TargetFolder =>
        SelectedNode is null ? "" : SelectedNode.IsFolder ? SelectedNode.Path : DocsService.ParentOf(SelectedNode.Path);

    // ------------------------------------------------------------------ open documents

    /// <summary>Documents open full-size, one tab each.</summary>
    public ObservableCollection<DocumentViewModel> Tabs { get; } = new();

    /// <summary>Documents open in small floating windows over the workspace.</summary>
    public ObservableCollection<DocWindowViewModel> Windows { get; } = new();

    private DocumentViewModel? _activeTab;
    public DocumentViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (!SetProperty(ref _activeTab, value)) return;
            if (previous != null) previous.IsActive = false;
            if (value != null) value.IsActive = true;
            OnPropertyChanged(nameof(HasActiveTab));
            OnPropertyChanged(nameof(InfoDocument));
            if (value != null)
            {
                if (_isInfoVisible) value.RefreshInfo();
                SelectInTree(value.Path);
            }
        }
    }

    public bool HasActiveTab => _activeTab != null;
    public bool HasTabs => Tabs.Count > 0;
    public bool HasWindows => Windows.Count > 0;
    /// <summary>Nothing is open and the graph is hidden: the workspace shows a short guide.</summary>
    public bool IsWorkspaceEmpty => Tabs.Count == 0 && Windows.Count == 0 && !_isGraphVisible;

    public IEnumerable<DocumentViewModel> OpenDocuments => Tabs.Concat(Windows.Select(w => w.Document));

    public DocumentViewModel? FindOpen(string path) =>
        OpenDocuments.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Size of the workspace, told by the view: floating windows open on half of it.</summary>
    public double WorkspaceWidth { get; set; } = 1000;
    public double WorkspaceHeight { get; set; } = 600;

    private void RaiseOpenChanged()
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(IsWorkspaceEmpty));
    }

    private void SelectInTree(string path)
    {
        var node = FindNode(path);
        if (node is null || node == SelectedNode) return;
        for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        if (SelectedNode != null) SelectedNode.IsSelected = false;
        node.IsSelected = true;
        SelectedNode = node;
    }

    private void OpenNode(DocNodeViewModel? node, bool inWindow)
    {
        if (node is null) return;
        if (node.IsFolder)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }
        Open(node.Path, inWindow);
    }

    /// <summary>
    /// Opens a document: full-size in a tab, or in a floating window on half of the workspace.
    /// Files that the app cannot show are opened with their Windows application.
    /// </summary>
    public DocumentViewModel? Open(string path, bool inWindow = false)
    {
        var existing = FindOpen(path);
        if (existing != null)
        {
            if (inWindow && Tabs.Contains(existing)) OpenInWindow(existing);
            else Activate(existing);
            return existing;
        }

        var node = Service.Find(path);
        if (node is null || node.IsFolder)
        {
            Refresh();
            return null;
        }
        if (node.Kind == DocKind.Other)
        {
            OpenExternal(path);
            return null;
        }

        var document = new DocumentViewModel(this, node, Settings.OpenInPreview);
        if (document.HasError) _dialogs.Error("The document could not be opened.\n\n" + document.Error);
        if (inWindow) AddWindow(document);
        else
        {
            Tabs.Add(document);
            IsGraphVisible = false;
            ActiveTab = document;
        }
        RaiseOpenChanged();
        return document;
    }

    private void Activate(DocumentViewModel document)
    {
        if (Tabs.Contains(document))
        {
            IsGraphVisible = false;
            ActiveTab = document;
        }
        else if (Windows.FirstOrDefault(w => w.Document == document) is { } window)
        {
            BringToFront(window);
        }
    }

    private void AddWindow(DocumentViewModel document)
    {
        // Half of the workspace, on the right; each new window is shifted a little.
        var shift = (Windows.Count % 6) * 26;
        var width = Math.Max(320, WorkspaceWidth / 2 - 16);
        var height = Math.Max(240, WorkspaceHeight - 24 - shift);
        var window = new DocWindowViewModel(this, document)
        {
            Width = width,
            Height = height,
            X = Math.Max(0, WorkspaceWidth - width - 12 - shift),
            Y = 12 + shift,
            ZIndex = ++_zCounter
        };
        Windows.Add(window);
        OnPropertyChanged(nameof(InfoDocument));
    }

    /// <summary>Moves a document from its tab into a floating window.</summary>
    public void OpenInWindow(DocumentViewModel document)
    {
        if (Windows.Any(w => w.Document == document)) return;
        document.Flush();
        var index = Tabs.IndexOf(document);
        if (index >= 0)
        {
            Tabs.RemoveAt(index);
            if (ActiveTab == document || ActiveTab is null) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        AddWindow(document);
        RaiseOpenChanged();
    }

    /// <summary>Moves a document from its floating window into a full-size tab.</summary>
    public void OpenAsTab(DocumentViewModel document)
    {
        document.Flush();
        var window = Windows.FirstOrDefault(w => w.Document == document);
        if (window != null) Windows.Remove(window);
        if (!Tabs.Contains(document)) Tabs.Add(document);
        IsGraphVisible = false;
        ActiveTab = document;
        RaiseOpenChanged();
    }

    public void BringToFront(DocWindowViewModel window)
    {
        if (window.ZIndex != _zCounter || _zCounter == 0) window.ZIndex = ++_zCounter;
    }

    /// <summary>
    /// Closes a document (it is saved first). When it cannot be saved the user decides whether to keep it
    /// open or to lose the changes; <paramref name="force"/> closes without asking (the file is being deleted).
    /// </summary>
    public void Close(DocumentViewModel document, bool force = false)
    {
        document.Flush();
        if (!document.Save() && !force &&
            !_dialogs.Confirm($"'{document.Name}' could not be saved.\n\nClose it anyway and lose the changes made since the last save?", "Close document"))
            return;
        var window = Windows.FirstOrDefault(w => w.Document == document);
        if (window != null) Windows.Remove(window);
        var index = Tabs.IndexOf(document);
        if (index >= 0)
        {
            Tabs.RemoveAt(index);
            if (ActiveTab == document || ActiveTab is null) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        RaiseOpenChanged();
        OnPropertyChanged(nameof(InfoDocument));
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Writes every modified document to disk. Returns false when something could not be saved.</summary>
    public bool SaveAll()
    {
        var ok = true;
        foreach (var document in OpenDocuments.ToList())
        {
            document.Flush();
            if (!document.Save()) ok = false;
        }
        return ok;
    }

    /// <summary>Called by the view on a timer: saves the documents that were not edited for a moment.</summary>
    public void AutoSave(double idleSeconds = 1.5)
    {
        var limit = DateTime.UtcNow.AddSeconds(-idleSeconds);
        foreach (var document in OpenDocuments.ToList())
        {
            if (!document.IsDirty || document.LastEditUtc > limit) continue;
            document.Save();
        }
    }

    /// <summary>Called by the view about once a second: autosave, then refresh of the info panel.</summary>
    public void Tick()
    {
        AutoSave();
        var idle = DateTime.UtcNow.AddSeconds(-1);
        foreach (var open in OpenDocuments.ToList())
            if (open.LastEditUtc < idle) open.RefreshStats();
        if (_isInfoVisible && InfoDocument is { IsDirty: false } document) document.RefreshInfo();
    }

    private DateTime _lastSaveError;
    private bool _saveErrorOpen;

    public void ReportSaveError(DocumentViewModel document, Exception ex)
    {
        StatusText = $"SAVE FAILED ({document.Name}): {ex.Message}";
        // The autosave retries every few seconds: one dialog at a time, and not at every attempt.
        if (_saveErrorOpen || (DateTime.UtcNow - _lastSaveError).TotalSeconds < 30) return;
        _saveErrorOpen = true;
        try
        {
            _dialogs.Error($"The document '{document.Name}' could not be saved.\n\n{ex.Message}\n\nIt stays open: the app keeps trying.");
        }
        finally
        {
            _saveErrorOpen = false;
            _lastSaveError = DateTime.UtcNow;
        }
    }

    /// <summary>Reloads the open documents that another program changed on disk.</summary>
    public void CheckExternalChanges()
    {
        foreach (var document in OpenDocuments.ToList())
        {
            try
            {
                if (document.IsEditable && document.ChangedOnDisk) document.Reload();   // markdown and rich text
            }
            catch { /* checked again next time */ }
        }
    }

    /// <summary>Called when the project is closed.</summary>
    public void Shutdown() => SaveAll();

    // ------------------------------------------------------------------ create / import

    public DocumentViewModel? NewDocument(string? suggestedName = null, string? folder = null, bool rich = false)
    {
        folder ??= TargetFolder;
        var vm = new NewDocumentViewModel(folder, suggestedName ?? "", rich);
        if (_dialogs.ShowDialog(vm) != true) return null;
        try
        {
            var title = DocsService.TitleOf(vm.Name.Trim());
            var path = rich
                ? Service.CreateRichDocument(folder, vm.Name)
                : Service.CreateDocument(folder, vm.Name, vm.Template.Render(title));
            Refresh(path);
            return Open(path);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The document could not be created.\n\n" + ex.Message);
            return null;
        }
    }

    private void NewFolder()
    {
        var name = _dialogs.Prompt("New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = Service.CreateFolder(TargetFolder, name);
            Refresh(path);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The folder could not be created.\n\n" + ex.Message);
        }
    }

    private void ImportWithDialog()
    {
        var files = _dialogs.PickFiles(
            "Documents (*.md;*.txt;*.pdf;*.docx;*.png;*.jpg)|*.md;*.markdown;*.txt;*.pdf;*.doc;*.docx;*.odt;*.rtf;*.png;*.jpg;*.jpeg;*.gif|All files (*.*)|*.*");
        if (files.Count > 0) Import(files, TargetFolder);
    }

    /// <summary>
    /// Adds external files or folders to the documentation. Depending on the project settings they are
    /// copied into the docs folder, linked to the original, or the user is asked.
    /// </summary>
    public void Import(IEnumerable<string> sources, string? folder = null, bool? forceLink = null)
    {
        var list = sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (list.Count == 0) return;
        folder ??= TargetFolder;

        bool link;
        if (forceLink != null) link = forceLink.Value;
        else
        {
            switch (Settings.ImportMode)
            {
                case DocImportMode.Link:
                    link = true;
                    break;
                case DocImportMode.Ask:
                    var answer = _dialogs.Ask(
                        $"Add {(list.Count == 1 ? "'" + System.IO.Path.GetFileName(list[0]) + "'" : list.Count + " items")} to the documentation.\n\n" +
                        "Yes = copy into the project (docs folder)\n" +
                        "No = keep the original where it is and create a link to it",
                        "Import documents");
                    if (answer is null) return;
                    link = answer == false;
                    break;
                default:
                    link = false;
                    break;
            }
        }

        string? last = null;
        var failed = new List<string>();
        foreach (var source in list)
        {
            try
            {
                last = Service.Import(source, folder, link);
            }
            catch (Exception ex)
            {
                failed.Add($"{System.IO.Path.GetFileName(source)}: {ex.Message}");
            }
        }
        Refresh(last);
        if (last != null) StatusText = $"{list.Count - failed.Count} item(s) {(link ? "linked" : "copied")}";
        if (failed.Count > 0) _dialogs.Error("Some items could not be imported:\n\n" + string.Join("\n", failed));
    }

    // ------------------------------------------------------------------ rename / move / delete

    private void Rename(DocNodeViewModel? node)
    {
        if (node is null) return;
        var current = node.IsDocument ? DocsService.TitleOf(node.Node.Name) : node.Node.Name;
        var name = _dialogs.Prompt(node.IsFolder ? "Rename folder" : "Rename document", "New name:", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        Relocate(node.Path, () => Service.Rename(node.Path, name));
    }

    /// <summary>Drag &amp; drop in the tree: moves an item into a folder ("" = top level).</summary>
    public void MoveNode(DocNodeViewModel node, string targetFolder)
    {
        if (DocsService.ParentOf(node.Path).Equals(targetFolder, StringComparison.OrdinalIgnoreCase)) return;
        Relocate(node.Path, () => Service.Move(node.Path, targetFolder));
    }

    /// <summary>Runs a rename / move, then points the open documents to the new paths and reloads them.</summary>
    private void Relocate(string oldPath, Func<string> operation)
    {
        if (!SaveAll()) return; // links are rewritten on disk: everything must be there first
        string newPath;
        try
        {
            newPath = operation();
        }
        catch (Exception ex)
        {
            _dialogs.Error(ex.Message);
            Refresh();
            return;
        }

        foreach (var document in OpenDocuments.ToList())
        {
            var path = document.Path;
            string? mapped = null;
            if (path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) mapped = newPath;
            else if (path.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) mapped = newPath + path[oldPath.Length..];
            if (mapped != null && Service.Find(mapped) is { } node) document.Retarget(node);
            if (document.IsText) document.Reload(); // its links may have been updated
        }
        Refresh(newPath);
        PathChanged?.Invoke(oldPath, newPath);
    }

    // ------------------------------------------------------------------ markdown <-> rich text

    /// <summary>
    /// Replaces a document with its conversion to the other format (.md to .rtf or back). What the
    /// other format cannot hold (colors in markdown, for example) is dropped; the original goes to docs/.trash.
    /// </summary>
    public void Convert(DocumentViewModel document)
    {
        if (!document.CanConvert) return;
        if (_converter is null)
        {
            _dialogs.Error("The conversion is not available here.");
            return;
        }
        var toRich = !document.IsRich;
        var question = toRich
            ? $"Convert '{document.Name}' to a rich text document (.rtf)?\n\n" +
              "Check boxes become text and links between documents become plain text."
            : $"Convert '{document.Name}' to a markdown document (.md)?\n\n" +
              "Formatting that markdown does not have (colors, fonts, sizes, underline, alignment) is lost.";
        if (!_dialogs.Confirm(question + "\n\nA copy of the original is kept in docs/.trash.", document.ConvertText)) return;

        document.Flush();
        if (!SaveAll()) return;

        var oldPath = document.Path;
        var inWindow = Windows.Any(w => w.Document == document);
        string newPath;
        try
        {
            var content = toRich
                ? _converter.MarkdownToRtf(document.Text, source => Service.ResolveImage(oldPath, source))
                : _converter.RtfToMarkdown(document.Text, bytes => DocsService.RelativeLink(oldPath, Service.SaveAsset(bytes)));
            newPath = Service.Convert(oldPath, content, toRich);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The document could not be converted.\n\n" + ex.Message);
            Refresh();
            return;
        }

        Close(document, force: true);
        foreach (var other in OpenDocuments.Where(d => d.IsText).ToList()) other.Reload(); // their links follow the new name
        Refresh(newPath);
        PathChanged?.Invoke(oldPath, newPath);
        Open(newPath, inWindow);
        StatusText = toRich ? "Converted to rich text" : "Converted to markdown";
    }

    private void Delete(DocNodeViewModel? node)
    {
        if (node is null) return;
        var message = node.IsFolder
            ? $"Delete the folder '{node.Node.Name}' and everything inside it?"
            : node.IsLink
                ? $"Remove the link '{node.Node.Name}'?\n\nThe original file is not touched."
                : $"Delete the document '{node.Node.Name}'?";
        if (!_dialogs.Confirm(message + "\n\nDeleted items are moved to docs/.trash and can be recovered from there.", "Delete")) return;

        foreach (var document in OpenDocuments.ToList())
        {
            if (document.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase) ||
                document.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase))
                Close(document, force: true);
        }
        var deleted = false;
        try
        {
            Service.Delete(node.Path);
            deleted = true;
        }
        catch (Exception ex)
        {
            _dialogs.Error("The item could not be deleted.\n\n" + ex.Message);
        }
        Refresh(DocsService.ParentOf(node.Path));
        if (deleted) PathRemoved?.Invoke(node.Path);
    }

    private void Reveal(DocNodeViewModel? node)
    {
        node ??= SelectedNode;
        try
        {
            Service.EnsureRoot();
            var path = node is null ? "" : node.IsFolder ? node.Path : DocsService.ParentOf(node.Path);
            _dialogs.OpenFolder(Service.FullPath(path));
        }
        catch (Exception ex)
        {
            _dialogs.Error(ex.Message);
        }
    }

    /// <summary>Opens a file of the documentation with its Windows application.</summary>
    public void OpenExternal(string path)
    {
        try
        {
            _dialogs.OpenExternal(Service.ContentPath(path));
        }
        catch (Exception ex)
        {
            _dialogs.Error("The file could not be opened.\n\n" + ex.Message);
        }
    }

    public void ExportHtml(DocumentViewModel document)
    {
        if (!document.IsText) return;
        document.Flush();
        var file = _dialogs.PickSaveFile(document.Title + ".html", "Web page (*.html)|*.html");
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            File.WriteAllText(file, MdDoc.ToHtml(MdDoc.Parse(document.Text), document.Title));
            StatusText = "Exported " + System.IO.Path.GetFileName(file);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The document could not be exported.\n\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ links

    /// <summary>
    /// A link was clicked in a document: web links open in the browser, links to other documents
    /// open them; a [[wiki link]] to a document that does not exist offers to create it.
    /// </summary>
    public void FollowLink(DocumentViewModel from, string href, bool isWiki)
    {
        href = (href ?? "").Trim();
        if (href.Length == 0) return;
        if (MdDoc.IsUrl(href))
        {
            _dialogs.OpenExternal(href);
            return;
        }

        var hash = href.IndexOf('#');
        var target = hash >= 0 ? href[..hash] : href;
        var anchor = hash >= 0 ? href[(hash + 1)..] : null;
        var inWindow = Windows.Any(w => w.Document == from);

        DocumentViewModel? opened;
        if (target.Length == 0) opened = from; // "#heading": a place in the same document
        else
        {
            var node = Service.Index().Resolve(from.Path, target, isWiki);
            if (node != null) opened = Open(node.Path, inWindow);
            else
            {
                if (!isWiki)
                {
                    _dialogs.Info($"The linked file was not found:\n{target}", "Documentation");
                    return;
                }
                var name = DocsService.FileNameOf(target.Replace('\\', '/'));
                if (!_dialogs.Confirm($"The document '{name}' does not exist yet.\n\nCreate it?", "Documentation")) return;
                try
                {
                    var path = Service.CreateDocument(DocsService.ParentOf(from.Path), name, $"# {DocsService.TitleOf(name)}\n\n");
                    Refresh(path);
                    opened = Open(path, inWindow);
                }
                catch (Exception ex)
                {
                    _dialogs.Error("The document could not be created.\n\n" + ex.Message);
                    return;
                }
            }
        }

        if (opened != null && !string.IsNullOrWhiteSpace(anchor))
        {
            opened.RefreshInfo(force: true);
            var heading = opened.Outline.FirstOrDefault(o => o.Text.Trim().Equals(anchor.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (heading != null) opened.GoToHeadingCommand.Execute(heading);
        }
    }

    /// <summary>Lets the user choose a document and returns the text of a [[wiki link]] to it.</summary>
    public string? PickLink(DocumentViewModel from)
    {
        var picker = new DocPickerViewModel(Service.Files().Where(f => f.Path != from.Path));
        if (_dialogs.ShowDialog(picker) != true || picker.Selected is null) return null;
        return WikiTarget(picker.Selected);
    }

    /// <summary>The shortest unambiguous wiki target of a document: its name, or its path when the name is not unique.</summary>
    public string WikiTarget(DocNode node)
    {
        var name = node.Kind == DocKind.Text ? node.Title : node.Name;
        var same = Service.Files().Count(f => string.Equals(f.Kind == DocKind.Text ? f.Title : f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same <= 1) return name;
        return node.Kind == DocKind.Text ? DocsService.TitleOf(node.DisplayPath) : node.DisplayPath;
    }

    // ------------------------------------------------------------------ search

    public ObservableCollection<DocSearchHit> SearchResults { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(IsSearching));
            OnPropertyChanged(nameof(IsBrowsing));
            RunSearch();
        }
    }

    /// <summary>True while a search text is typed: the tree is replaced by the results.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);
    public bool IsBrowsing => !IsSearching;

    private string _searchSummary = "";
    public string SearchSummary { get => _searchSummary; private set => SetProperty(ref _searchSummary, value); }

    private void RunSearch()
    {
        SearchResults.Clear();
        if (!IsSearching) return;
        SaveAll(); // search what the user sees
        try
        {
            foreach (var hit in Service.Search(_searchText)) SearchResults.Add(hit);
        }
        catch (Exception ex)
        {
            SearchSummary = ex.Message;
            return;
        }
        SearchSummary = SearchResults.Count == 0 ? "No results" : $"{SearchResults.Count} result{(SearchResults.Count == 1 ? "" : "s")}";
    }

    private void OpenHit(DocSearchHit? hit)
    {
        if (hit is null) return;
        var document = Open(hit.Path);
        if (document != null && hit.Line >= 0) document.GoToHeadingCommand.Execute(new MdOutlineItem { Line = hit.Line, Index = -1, Text = hit.Snippet });
    }

    // ------------------------------------------------------------------ info panel

    private bool _isInfoVisible;
    /// <summary>Side panel with the outline, the backlinks and the statistics of the active document.</summary>
    public bool IsInfoVisible
    {
        get => _isInfoVisible;
        set
        {
            if (!SetProperty(ref _isInfoVisible, value)) return;
            if (value) InfoDocument?.RefreshInfo(force: true);
        }
    }

    /// <summary>The document described by the info panel.</summary>
    public DocumentViewModel? InfoDocument => _activeTab;

    // ------------------------------------------------------------------ graph

    private bool _isGraphVisible;
    /// <summary>Shows the graph of the references between documents instead of the open document.</summary>
    public bool IsGraphVisible
    {
        get => _isGraphVisible;
        set
        {
            if (!SetProperty(ref _isGraphVisible, value)) return;
            if (value) RebuildGraph();
            OnPropertyChanged(nameof(IsWorkspaceEmpty));
            OnPropertyChanged(nameof(IsEditorVisible));
        }
    }

    public bool IsEditorVisible => !_isGraphVisible;

    private GraphModel _graph = new();
    public GraphModel Graph { get => _graph; private set => SetProperty(ref _graph, value); }

    public string GraphSummary => $"{_graph.Nodes.Count} documents · {_graph.Edges.Count} links";

    public void RebuildGraph()
    {
        SaveAll(); // the graph is built from the files
        try
        {
            var model = GraphModel.FromDocs(Service.BuildGraph());
            model.Run(220); // open already laid out; the view keeps animating the last steps
            model.Alpha = Math.Max(model.Alpha, 0.05);
            Graph = model;
        }
        catch (Exception ex)
        {
            Graph = new GraphModel();
            StatusText = "The graph could not be built: " + ex.Message;
        }
        OnPropertyChanged(nameof(GraphSummary));
        SelectGraphNode(_graphNodeId != null && _graph.Find(_graphNodeId) != null ? _graphNodeId : null);
    }

    private string? _graphNodeId;
    /// <summary>The node clicked in the graph: its preview is shown beside the graph.</summary>
    public string? GraphNodeId { get => _graphNodeId; private set => SetProperty(ref _graphNodeId, value); }

    public bool HasGraphSelection => _graphNodeId != null;
    public bool IsGraphGhost => _graphNodeId != null && _graphNodeId.StartsWith('?');
    public bool IsGraphDocument => _graphNodeId != null && !_graphNodeId.StartsWith('?');

    private string _graphTitle = "";
    public string GraphTitle { get => _graphTitle; private set => SetProperty(ref _graphTitle, value); }

    private string _graphInfo = "";
    public string GraphInfo { get => _graphInfo; private set => SetProperty(ref _graphInfo, value); }

    private string _graphMarkdown = "";
    /// <summary>Markdown of the selected document, rendered by the preview panel.</summary>
    public string GraphMarkdown { get => _graphMarkdown; private set => SetProperty(ref _graphMarkdown, value); }

    public void SelectGraphNode(string? id)
    {
        GraphNodeId = id;
        var node = id is null ? null : _graph.Find(id);
        if (node is null)
        {
            GraphNodeId = null;
            GraphTitle = "";
            GraphInfo = "";
            GraphMarkdown = "";
        }
        else if (node.IsGhost)
        {
            GraphTitle = node.Label;
            GraphInfo = $"Referenced by {node.Degree} document{(node.Degree == 1 ? "" : "s")}, but it does not exist yet.";
            GraphMarkdown = "";
        }
        else
        {
            GraphTitle = node.Label;
            GraphInfo = $"{node.Id}  ·  {node.Degree} link{(node.Degree == 1 ? "" : "s")}";
            try
            {
                var open = FindOpen(node.Id);
                var text = open != null ? open.Text : Service.Read(node.Id);
                // The preview is drawn as markdown: of a rich text document it shows the plain text.
                GraphMarkdown = RtfText.LooksLikeRtf(text) ? RtfText.ToPlain(text) : text;
            }
            catch (Exception ex)
            {
                GraphMarkdown = "_" + ex.Message.Replace("\n", " ") + "_";
            }
        }
        OnPropertyChanged(nameof(HasGraphSelection));
        OnPropertyChanged(nameof(IsGraphGhost));
        OnPropertyChanged(nameof(IsGraphDocument));
    }

    private void OpenGraphSelection(bool inWindow)
    {
        if (IsGraphDocument) Open(_graphNodeId!, inWindow);
    }

    private void CreateGraphGhost()
    {
        if (!IsGraphGhost) return;
        var node = _graph.Find(_graphNodeId!);
        if (node is null) return;
        try
        {
            var path = Service.CreateDocument("", node.Label, $"# {DocsService.TitleOf(node.Label)}\n\n");
            Refresh(path);
            Open(path);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The document could not be created.\n\n" + ex.Message);
        }
    }
}
