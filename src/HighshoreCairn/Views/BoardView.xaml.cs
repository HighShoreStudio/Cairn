using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Board screen. The code-behind only contains view concerns: mouse handling for
/// drag &amp; drop and double-click. Every change goes through the BoardViewModel.
/// </summary>
public partial class BoardView : UserControl
{
    private const string CardFormat = "HighshoreCairn.Card";

    private Point _dragStart;
    private CardViewModel? _pressedCard;
    private CardViewModel? _markedCard;
    private ColumnViewModel? _markedColumn;

    private BoardViewModel? _attached;

    public BoardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as BoardViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as BoardViewModel);
    }

    private BoardViewModel? Board => DataContext as BoardViewModel;

    private void Attach(BoardViewModel? board)
    {
        if (ReferenceEquals(_attached, board)) return;
        if (_attached != null) _attached.CardFocusRequested -= OnCardFocusRequested;
        _attached = board;
        if (_attached != null) _attached.CardFocusRequested += OnCardFocusRequested;
    }

    /// <summary>"Go to task" from a reference: scrolls the board and the column so the marked card is on screen.</summary>
    private void OnCardFocusRequested(CardViewModel card)
    {
        // The Kanban area may have just become visible: wait until it has been laid out.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindCardElement(BoardScroll, card) is { } element) element.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static FrameworkElement? FindCardElement(DependencyObject root, CardViewModel card)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { DataContext: CardViewModel data } border && ReferenceEquals(data, card)) return border;
            if (FindCardElement(child, card) is { } nested) return nested;
        }
        return null;
    }

    // ------------------------------------------------------------------ card mouse handling

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CardViewModel card }) return;
        Board?.ClearHighlight(); // the mark left by "go to task" has done its job

        if (e.ClickCount == 2)
        {
            // Double-click: edit. Deferred so the dialog opens after the mouse event is over.
            _pressedCard = null;
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => card.EditCommand.Execute(null)));
            return;
        }

        _pressedCard = card;
        _dragStart = e.GetPosition(this);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pressedCard = null;

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedCard = null;
            return;
        }

        // Start dragging only after the mouse moved a few pixels (otherwise it is a click).
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var card = _pressedCard;
        _pressedCard = null;
        card.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(CardFormat, card), DragDropEffects.Move);
        }
        finally
        {
            card.IsDragging = false;
            ClearMarks();
        }
    }

    // ------------------------------------------------------------------ drop targets (columns)

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(CardFormat) ||
            sender is not FrameworkElement { DataContext: ColumnViewModel column } element)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var dragged = e.Data.GetData(CardFormat) as CardViewModel;
        var anchor = FindAnchor(element, e);
        Mark(column, anchor, dragged);
        AutoScroll(element, e);
    }

    private void Column_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving over child elements: clear only when really outside.
        if (sender is not FrameworkElement element) return;
        var position = e.GetPosition(element);
        if (position.X < 0 || position.Y < 0 || position.X > element.ActualWidth || position.Y > element.ActualHeight)
            ClearMarks();
    }

    private void Column_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (Board is null ||
                e.Data.GetData(CardFormat) is not CardViewModel card ||
                sender is not FrameworkElement { DataContext: ColumnViewModel column } element) return;

            var anchor = FindAnchor(element, e);
            ClearMarks();
            Board.MoveCard(card, column, anchor, after: false);
        }
        finally
        {
            ClearMarks();
        }
    }

    /// <summary>
    /// Finds the card before which the dragged card must be inserted:
    /// the first card whose vertical middle is below the mouse. null = end of the column.
    /// </summary>
    private static CardViewModel? FindAnchor(FrameworkElement columnElement, DragEventArgs e)
    {
        var list = FindCardList(columnElement);
        if (list is null) return null;

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            if (e.GetPosition(container).Y < container.ActualHeight / 2)
                return list.Items[i] as CardViewModel;
        }
        return null;
    }

    private static ItemsControl? FindCardList(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ItemsControl { DataContext: ColumnViewModel } items) return items;
            var nested = FindCardList(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>Scrolls the column (vertically) and the board (horizontally) while dragging near an edge.</summary>
    private void AutoScroll(FrameworkElement columnElement, DragEventArgs e)
    {
        const double edge = 36;
        const double step = 14;

        var columnScroll = FindScrollViewer(columnElement);
        if (columnScroll != null)
        {
            var y = e.GetPosition(columnScroll).Y;
            if (y < edge) columnScroll.ScrollToVerticalOffset(columnScroll.VerticalOffset - step);
            else if (y > columnScroll.ActualHeight - edge) columnScroll.ScrollToVerticalOffset(columnScroll.VerticalOffset + step);
        }

        var x = e.GetPosition(BoardScroll).X;
        if (x < edge) BoardScroll.ScrollToHorizontalOffset(BoardScroll.HorizontalOffset - step);
        else if (x > BoardScroll.ActualWidth - edge) BoardScroll.ScrollToHorizontalOffset(BoardScroll.HorizontalOffset + step);
    }

    // ------------------------------------------------------------------ drop feedback

    private void Mark(ColumnViewModel column, CardViewModel? anchor, CardViewModel? dragged)
    {
        // Where the insertion line is drawn: above the anchor, or below the last card.
        var target = anchor ?? column.VisibleCards.LastOrDefault();
        var below = anchor is null;
        if (target == dragged) target = null;

        if (_markedColumn != column)
        {
            if (_markedColumn != null) _markedColumn.IsDropTarget = false;
            _markedColumn = column;
            column.IsDropTarget = true;
        }

        if (_markedCard != null && _markedCard != target)
            _markedCard.DropAbove = _markedCard.DropBelow = false;

        _markedCard = target;
        if (target != null)
        {
            target.DropAbove = !below;
            target.DropBelow = below;
        }
    }

    private void ClearMarks()
    {
        if (_markedColumn != null) _markedColumn.IsDropTarget = false;
        if (_markedCard != null) _markedCard.DropAbove = _markedCard.DropBelow = false;
        _markedColumn = null;
        _markedCard = null;
    }

    // ------------------------------------------------------------------ column menu button

    private void ColumnMenu_Click(object sender, RoutedEventArgs e)
    {
        // Opens the context menu of the column header from the "..." button.
        if (sender is not FrameworkElement { Tag: FrameworkElement header } button ||
            header.ContextMenu is not { } menu) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;

        // Back to the normal right-click behaviour once the menu closes.
        RoutedEventHandler? restore = null;
        restore = (_, _) =>
        {
            menu.Closed -= restore;
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = header;
        };
        menu.Closed += restore;
        menu.IsOpen = true;
    }
}
