using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Documentation screen. The logic lives in DocsViewModel; here only what needs the mouse or WPF
/// objects: drag &amp; drop in the tree, the floating windows, the graph preview, the autosave timer.
/// </summary>
public partial class DocsView : UserControl
{
    private const string NodeFormat = "HighshoreCairn.DocNode";

    private DocsViewModel? _vm;
    private readonly DispatcherTimer _timer;
    private Point _dragStart;
    private DocNodeViewModel? _dragNode;
    private DocNodeViewModel? _dropTarget;
    private Window? _window;

    public DocsView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _vm?.Tick();

        DataContextChanged += (_, _) => Attach(DataContext as DocsViewModel);
        Loaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = Window.GetWindow(this);
            if (_window != null) _window.Activated += OnWindowActivated;
            UpdateTimer();
        };
        Unloaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = null;
            _timer.Stop();
            _vm?.SaveAll();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();

        Graph.NodeSelected += id => _vm?.SelectGraphNode(id);
        Graph.NodeActivated += id =>
        {
            if (_vm is null || id.StartsWith('?')) return;
            _vm.Open(id);
        };
    }

    private void Attach(DocsViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        PushWorkspaceSize();
        UpdateGraphPreview();
        UpdateTreeColumn();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        // Autosave runs only while the documentation is on screen (leaving it saves everything).
        if (_vm != null && IsVisible) _timer.Start();
        else _timer.Stop();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Coming back from another program: pick up the files it changed.
        if (IsVisible) _vm?.CheckExternalChanges();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocsViewModel.GraphMarkdown)) UpdateGraphPreview();
        else if (e.PropertyName == nameof(DocsViewModel.IsTreeVisible)) UpdateTreeColumn();
    }

    private GridLength _treeWidth = new(270);

    /// <summary>Hiding the tree gives its column (and the splitter) to the document.</summary>
    private void UpdateTreeColumn()
    {
        if (_vm is null) return;
        if (_vm.IsTreeVisible)
        {
            TreeColumn.MinWidth = 180;
            TreeColumn.Width = _treeWidth;
            SplitterColumn.Width = new GridLength(8);
        }
        else
        {
            if (TreeColumn.Width.Value > 0) _treeWidth = TreeColumn.Width;
            TreeColumn.MinWidth = 0;
            TreeColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
    }

    private void OnThemeChanged() => UpdateGraphPreview();

    // ------------------------------------------------------------------ graph

    private void UpdateGraphPreview()
    {
        if (_vm is null) return;
        try
        {
            var style = FlowStyle.From(this, 13);
            var path = _vm.GraphNodeId ?? "";
            var document = MarkdownFlow.Build(MdDoc.Parse(_vm.GraphMarkdown), style, source => _vm.Service.ResolveImage(path, source));
            document.Foreground = style.Text;
            GraphPreview.Document = document;
        }
        catch
        {
            GraphPreview.Document = null;
        }
    }

    private void Graph_ZoomIn(object sender, RoutedEventArgs e) => Graph.ZoomBy(1.25);
    private void Graph_ZoomOut(object sender, RoutedEventArgs e) => Graph.ZoomBy(1 / 1.25);
    private void Graph_Fit(object sender, RoutedEventArgs e) => Graph.Fit();

    // ------------------------------------------------------------------ workspace

    private void PushWorkspaceSize()
    {
        if (_vm is null || Workspace.ActualWidth <= 0) return;
        _vm.WorkspaceWidth = Workspace.ActualWidth;
        _vm.WorkspaceHeight = Workspace.ActualHeight;
    }

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PushWorkspaceSize();
        if (_vm is null) return;
        // Windows stay reachable when the workspace shrinks.
        foreach (var window in _vm.Windows) Clamp(window);
    }

    private void Clamp(DocWindowViewModel window)
    {
        var maxX = Math.Max(0, Workspace.ActualWidth - 80);
        var maxY = Math.Max(0, Workspace.ActualHeight - 30);
        window.X = Math.Clamp(window.X, Math.Min(0, 80 - window.Width), maxX);
        window.Y = Math.Clamp(window.Y, 0, maxY);
    }

    private void Workspace_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>Files dropped on the workspace are added to the documentation and opened.</summary>
    private void Workspace_Drop(object sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        // After the drop has returned: a question asked here would keep Explorer waiting.
        var vm = _vm;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            vm.Import(files, vm.TargetFolder);
            if (vm.SelectedNode is { IsFolder: false } node) vm.Open(node.Path);
        }));
    }

    // ------------------------------------------------------------------ tabs

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.DataContext is not DocumentViewModel document) return;
        _vm.IsGraphVisible = false;
        _vm.ActiveTab = document;
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle click closes the tab.
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.DataContext is not DocumentViewModel document) return;
        document.CloseCommand.Execute(null);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ floating windows

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocWindowViewModel window) _vm?.BringToFront(window);
    }

    private void Window_Move(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocWindowViewModel window) return;
        window.X += e.HorizontalChange;
        window.Y += e.VerticalChange;
        Clamp(window);
    }

    private void Window_Resize(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocWindowViewModel window) return;
        window.Width = Math.Min(window.Width + e.HorizontalChange, Math.Max(280, Workspace.ActualWidth - window.X));
        window.Height = Math.Min(window.Height + e.VerticalChange, Math.Max(180, Workspace.ActualHeight - window.Y));
    }

    // ------------------------------------------------------------------ tree

    private static DocNodeViewModel? NodeAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: DocNodeViewModel node }) return node;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null) _vm.SelectedNode = e.NewValue as DocNodeViewModel;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || NodeAt(e.OriginalSource as DependencyObject) is not { IsFolder: false } node) return;
        var inWindow = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (inWindow) _vm.OpenInWindowCommand.Execute(node);
        else _vm.OpenCommand.Execute(node);
        e.Handled = true;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        switch (e.Key)
        {
            case Key.Enter when !node.IsFolder:
                _vm.OpenCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.F2:
                _vm.RenameCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Delete:
                _vm.DeleteCommand.Execute(node);
                e.Handled = true;
                break;
        }
    }

    /// <summary>A right click selects the item first, so the context menu acts on what was clicked.</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (NodeAt(e.OriginalSource as DependencyObject) is { } node) node.IsSelected = true;
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragNode = e.OriginalSource is ToggleButton ? null : NodeAt(e.OriginalSource as DependencyObject);
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var node = _dragNode;
        _dragNode = null;
        try
        {
            DragDrop.DoDragDrop(Tree, new DataObject(NodeFormat, node), DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    private void SetDropTarget(DocNodeViewModel? node)
    {
        if (_dropTarget == node) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = node;
        if (_dropTarget != null) _dropTarget.IsDropTarget = true;
    }

    /// <summary>The folder a drop would go into: the folder under the mouse, or the folder of the file under it.</summary>
    private static (DocNodeViewModel? Folder, string Path) DropFolder(DragEventArgs e)
    {
        var node = NodeAt(e.OriginalSource as DependencyObject);
        if (node is null) return (null, "");
        if (node.IsFolder) return (node, node.Path);
        return (node.Parent, DocsService.ParentOf(node.Path));
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var (folder, path) = DropFolder(e);
        e.Handled = true;

        if (e.Data.GetData(NodeFormat) is DocNodeViewModel dragged)
        {
            var same = DocsService.ParentOf(dragged.Path).Equals(path, StringComparison.OrdinalIgnoreCase);
            var intoItself = path.Equals(dragged.Path, StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith(dragged.Path + "/", StringComparison.OrdinalIgnoreCase);
            e.Effects = same || intoItself ? DragDropEffects.None : DragDropEffects.Move;
            SetDropTarget(e.Effects == DragDropEffects.None ? null : folder);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            SetDropTarget(folder);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (_vm is null) return;
        var (_, path) = DropFolder(e);
        e.Handled = true;

        var vm = _vm;
        if (e.Data.GetData(NodeFormat) is DocNodeViewModel dragged) Dispatcher.BeginInvoke(new Action(() => vm.MoveNode(dragged, path)));
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Dispatcher.BeginInvoke(new Action(() => vm.Import(files, path)));
    }

    private void Search_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: DocSearchHit hit } list) return;
        _vm?.OpenHitCommand.Execute(hit);
        list.SelectedItem = null;
    }

    // ------------------------------------------------------------------ context menu of the tree

    private static DocNodeViewModel? MenuNode(object sender) => (sender as FrameworkElement)?.DataContext as DocNodeViewModel;

    private void Menu_Open(object sender, RoutedEventArgs e) => _vm?.OpenCommand.Execute(MenuNode(sender));
    private void Menu_OpenInWindow(object sender, RoutedEventArgs e) => _vm?.OpenInWindowCommand.Execute(MenuNode(sender));
    private void Menu_OpenExternal(object sender, RoutedEventArgs e) => _vm?.OpenExternalCommand.Execute(MenuNode(sender));
    private void Menu_Rename(object sender, RoutedEventArgs e) => _vm?.RenameCommand.Execute(MenuNode(sender));
    private void Menu_Delete(object sender, RoutedEventArgs e) => _vm?.DeleteCommand.Execute(MenuNode(sender));
    private void Menu_Reveal(object sender, RoutedEventArgs e) => _vm?.RevealCommand.Execute(MenuNode(sender));

    private void Menu_NewDocument(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MenuNode(sender) is { } node) _vm.SelectedNode = node;
        _vm.NewDocumentCommand.Execute(null);
    }

    private void Menu_NewRichDocument(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MenuNode(sender) is { } node) _vm.SelectedNode = node;
        _vm.NewRichDocumentCommand.Execute(null);
    }

    /// <summary>The "New" button opens its little menu (Document / Rich Text) right below itself.</summary>
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void New_Document(object sender, RoutedEventArgs e) => _vm?.NewDocumentCommand.Execute(null);

    private void New_RichText(object sender, RoutedEventArgs e) => _vm?.NewRichDocumentCommand.Execute(null);

    private void Menu_NewFolder(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MenuNode(sender) is { } node) _vm.SelectedNode = node;
        _vm.NewFolderCommand.Execute(null);
    }
}
