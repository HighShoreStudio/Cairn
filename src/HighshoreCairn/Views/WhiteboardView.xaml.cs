using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Whiteboard screen: the tool bar (XAML) around the WhiteboardCanvas control, plus the graph view of
/// the linked elements with the preview of the clicked one.
/// </summary>
public partial class WhiteboardView : UserControl
{
    private WhiteboardViewModel? _vm;

    public WhiteboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as WhiteboardViewModel);
        Graph.NodeSelected += id => _vm?.SelectGraphNode(id);
        Graph.NodeActivated += id =>
        {
            if (_vm is null) return;
            _vm.SelectGraphNode(id);
            _vm.ShowOnBoardCommand.Execute(null);
        };
        Loaded += (_, _) => { ThemeService.ThemeChanged -= UpdatePreview; ThemeService.ThemeChanged += UpdatePreview; };
        Unloaded += (_, _) => ThemeService.ThemeChanged -= UpdatePreview;
    }

    private void Attach(WhiteboardViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.GraphDecorationsChanged -= OnGraphDecorationsChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.GraphDecorationsChanged += OnGraphDecorationsChanged;
        }
        UpdatePreview();
    }

    /// <summary>A reference was added or removed: the dot of the node appears or goes away.</summary>
    private void OnGraphDecorationsChanged() => Graph.InvalidateVisual();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WhiteboardViewModel.GraphElement)) UpdatePreview();
    }

    /// <summary>The preview is the element itself, drawn by the canvas as a picture.</summary>
    private void UpdatePreview()
    {
        GraphPreview.Source = _vm?.IsGraphMode == true ? Board.RenderElement(_vm.GraphElement, 300) : null;
    }

    private void Graph_ZoomIn(object sender, RoutedEventArgs e) => Graph.ZoomBy(1.25);
    private void Graph_ZoomOut(object sender, RoutedEventArgs e) => Graph.ZoomBy(1 / 1.25);
    private void Graph_Fit(object sender, RoutedEventArgs e) => Graph.Fit();
}
