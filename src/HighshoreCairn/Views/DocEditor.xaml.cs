using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Editor of one document. The ViewModel holds the markdown text; this control shows it either as
/// source (a TextBox bound to the text) or as formatted text (a RichTextBox whose content is built
/// from the markdown and written back to it a moment after every change).
/// </summary>
public partial class DocEditor : UserControl
{
    private DocumentViewModel? _vm;
    private FlowStyle _style = new();
    private readonly DispatcherTimer _syncTimer;
    private bool _building;        // the formatted view is being rebuilt: its changes are not edits
    private bool _previewDirty;    // the formatted view has edits that are not in the markdown text yet
    private bool _normalizePending;

    public DocEditor()
    {
        InitializeComponent();
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _syncTimer.Tick += (_, _) => { _syncTimer.Stop(); PushPreview(); };

        DataContextChanged += (_, _) => Attach(DataContext as DocumentViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataObject.AddPastingHandler(Rich, OnPaste);
        // Ticking a task: check boxes restored by Undo have no handler of their own, so listen here.
        Rich.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => MarkPreviewDirty()));
        Rich.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => MarkPreviewDirty()));

        // Formatting that markdown cannot store is switched off in the formatted view.
        foreach (var command in new[]
                 {
                     EditingCommands.ToggleUnderline, EditingCommands.AlignCenter, EditingCommands.AlignJustify,
                     EditingCommands.AlignLeft, EditingCommands.AlignRight, EditingCommands.IncreaseFontSize,
                     EditingCommands.DecreaseFontSize, EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript
                 })
        {
            Rich.CommandBindings.Add(new CommandBinding(command, (_, e) => e.Handled = true,
                (_, e) => { e.CanExecute = false; e.Handled = true; }));
        }
    }

    private DocsViewModel? Owner => _vm?.Owner;

    // ============================================================================ life cycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        if (_vm is null) Attach(DataContext as DocumentViewModel);
        TakePendingScroll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        _syncTimer.Stop();
        // Nothing typed is lost when the editor leaves the screen; and an editor that is gone must not
        // keep listening to its document (OnLoaded attaches again if it comes back).
        Attach(null);
    }

    private void Attach(DocumentViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            PushPreview();
            _vm.PropertyChanged -= OnVmChanged;
            _vm.FlushRequested -= OnFlushRequested;
            _vm.ScrollRequested -= ScrollTo;
            _vm.Reloaded -= OnReloaded;
        }
        _vm = vm;
        _previewDirty = false;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmChanged;
        _vm.FlushRequested += OnFlushRequested;
        _vm.ScrollRequested += ScrollTo;
        _vm.Reloaded += OnReloaded;
        if (_vm.IsPreview) BuildPreview();
        UpdateHint();
        if (IsLoaded) TakePendingScroll();
    }

    private void TakePendingScroll()
    {
        if (_vm?.PendingScroll is not { } item) return;
        _vm.PendingScroll = null;
        Dispatcher.BeginInvoke(new Action(() => ScrollTo(item)), DispatcherPriority.ContextIdle);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(DocumentViewModel.IsPreview))
        {
            if (_vm.IsPreview) BuildPreview();
            UpdateHint();
            var vm = _vm;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(vm, _vm)) return;
                if (vm.IsPreview) Rich.Focus(); else Source.Focus();
            }), DispatcherPriority.Input);
        }
        else if (e.PropertyName is nameof(DocumentViewModel.IsDirty) or nameof(DocumentViewModel.IsPreviewLocked))
        {
            UpdateHint();
            if (e.PropertyName == nameof(DocumentViewModel.IsPreviewLocked)) ApplyPreviewLock();
        }
    }

    private void UpdateHint()
    {
        if (_vm is null) return;
        HintText.Text = (_vm.IsDirty ? "Saving…" : "Saved") +
                        (_vm.IsLink ? "  ·  linked file" : "") +
                        (_vm.IsPreview ? (_vm.IsPreviewLocked ? "  ·  read-only" : "") + "  ·  Ctrl+Click opens a link" : "");
    }

    private void OnReloaded()
    {
        _previewDirty = false;
        if (_vm is { IsPreview: true }) BuildPreview();
    }

    private void OnThemeChanged()
    {
        if (_vm is not { IsPreview: true }) return;
        PushPreview();
        BuildPreview(); // the document holds the brushes of the old theme
    }

    private void OnFlushRequested()
    {
        _syncTimer.Stop();
        PushPreview();
    }

    // ============================================================================ markdown <-> formatted view

    private string? ResolveImage(string source) =>
        _vm is null ? null : _vm.Owner.Service.ResolveImage(_vm.Path, source);

    private void BuildPreview()
    {
        if (_vm is null) return;
        _building = true;
        try
        {
            _style = FlowStyle.From(this);
            Rich.Document = MarkdownFlow.Build(MdDoc.Parse(_vm.Text), _style, ResolveImage, MarkPreviewDirty);
        }
        finally
        {
            _building = false;
        }
        _previewDirty = false;
        ApplyPreviewLock();
    }

    /// <summary>
    /// A document with markdown the formatted view cannot keep (HTML, reference links, footnotes) is
    /// shown but not edited there: saving it would rewrite those parts as plain text.
    /// </summary>
    private void ApplyPreviewLock()
    {
        var locked = _vm?.IsPreviewLocked == true;
        Rich.IsReadOnly = locked;
        Rich.IsDocumentEnabled = !locked;
    }

    private void MarkPreviewDirty()
    {
        if (_building || _vm is null || !_vm.IsPreview || _vm.IsPreviewLocked) return;
        _previewDirty = true;
        _syncTimer.Stop();
        _syncTimer.Start();
    }

    /// <summary>Writes the edits made in the formatted view into the markdown text of the ViewModel.</summary>
    private void PushPreview()
    {
        if (!_previewDirty || _vm is null) return;
        _previewDirty = false;
        if (_vm.IsPreviewLocked) return;
        string markdown;
        try
        {
            markdown = MdDoc.Write(MarkdownFlow.Read(Rich.Document));
        }
        catch (Exception ex)
        {
            // Never replace the last good text because of an unexpected document shape: tell, and try again later.
            _previewDirty = true;
            HintText.Text = "Not saved yet: " + ex.Message;
            return;
        }
        if (markdown != _vm.Text) _vm.Text = markdown;
    }

    private void Rich_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_building) return;
        MarkPreviewDirty();
        // Undo / redo must be left alone: adding a check box back would cancel the undo.
        if (e.UndoAction is UndoAction.Undo or UndoAction.Redo) return;
        if (_normalizePending) return;
        _normalizePending = true;
        Dispatcher.BeginInvoke(new Action(NormalizeTaskLists), DispatcherPriority.Background);
    }

    /// <summary>Every item of a task list gets its check box (new items are created by the Enter key).</summary>
    private void NormalizeTaskLists()
    {
        _normalizePending = false;
        if (_vm is not { IsPreview: true, IsPreviewLocked: false }) return;
        foreach (var list in MarkdownFlow.Lists(Rich.Document.Blocks).Where(l => l.Tag as string == MarkdownFlow.TagTask).ToList())
        {
            foreach (var item in list.ListItems.ToList())
            {
                if (item.Tag as string == MarkdownFlow.TagNoBox) continue;
                if (item.Blocks.FirstBlock is not Paragraph first)
                {
                    first = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    if (item.Blocks.FirstBlock is null) item.Blocks.Add(first);
                    else item.Blocks.InsertBefore(item.Blocks.FirstBlock, first);
                }
                if (MarkdownFlow.HasCheckBox(first)) continue;
                var caretHere = Rich.CaretPosition.Paragraph == first;
                MarkdownFlow.AddCheckBox(first, false, MarkPreviewDirty);
                if (caretHere && new TextRange(first.ContentStart, Rich.CaretPosition).Text.Length == 0 && first.Inlines.FirstInline != null)
                    Rich.CaretPosition = first.Inlines.FirstInline.ElementEnd;
            }
        }
    }

    // ============================================================================ scrolling

    private void ScrollTo(MdOutlineItem item)
    {
        if (_vm is null) return;
        if (_vm.IsPreview)
        {
            var index = item.Index;
            if (index < 0)
            {
                // A line of the source (search result): go to the last heading above it.
                var outline = MdDoc.Outline(_vm.Text);
                index = outline.FindLastIndex(o => o.Line <= item.Line);
            }
            var headings = MarkdownFlow.TopLevelHeadings(Rich.Document);
            if (index < 0 || index >= headings.Count)
            {
                Rich.ScrollToHome();
                return;
            }
            var heading = headings[index];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var rect = heading.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    Rich.ScrollToVerticalOffset(Math.Max(0, Rich.VerticalOffset + rect.Top - 10));
                    Rich.CaretPosition = heading.ContentStart;
                }
                catch { /* the document changed meanwhile */ }
            }), DispatcherPriority.ContextIdle);
        }
        else
        {
            var line = item.Line;
            if (line < 0) return;
            var text = Source.Text;
            var position = 0;
            for (var i = 0; i < line && position >= 0; i++)
            {
                var next = text.IndexOf('\n', position);
                position = next < 0 ? -1 : next + 1;
            }
            if (position < 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Source.Focus();
                Source.CaretIndex = Math.Min(position, Source.Text.Length);
                var rect = Source.GetRectFromCharacterIndex(Source.CaretIndex);
                if (!rect.IsEmpty) Source.ScrollToVerticalOffset(Math.Max(0, Source.VerticalOffset + rect.Top - 10));
            }), DispatcherPriority.ContextIdle);
        }
    }

    // ============================================================================ toolbar

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: string action }) return;
        RunTool(action);
    }

    /// <summary>Runs a toolbar action in the view that is on screen; a failure is reported, never fatal.</summary>
    private void RunTool(string action)
    {
        if (_vm is null) return;
        try
        {
            if (!_vm.IsPreview) ApplySource(action);
            else if (_vm.IsPreviewLocked) Owner?.Dialogs.Info(_vm.PreviewLockText, "Preview");
            else ApplyRich(action);
        }
        catch (Exception ex)
        {
            Owner?.Dialogs.Error("The formatting could not be applied here.\n\n" + ex.Message);
        }
    }

    // ---------------------------------------------------------------------------- formatted view

    private void ApplyRich(string action)
    {
        Rich.Focus();
        // One toolbar action = one step of Undo. The actions that open a dialog are left out: a change block
        // must not stay open while a modal window is on screen.
        var grouped = action is "strike" or "code" or "p" or "h1" or "h2" or "h3" or "ul" or "ol" or "task" or "quote" or "codeblock" or "rule";
        if (grouped) Rich.BeginChange();
        try
        {
            ApplyRichCore(action);
        }
        finally
        {
            if (grouped) Rich.EndChange();
        }
        MarkPreviewDirty();
    }

    private void ApplyRichCore(string action)
    {
        switch (action)
        {
            case "bold": EditingCommands.ToggleBold.Execute(null, Rich); break;
            case "italic": EditingCommands.ToggleItalic.Execute(null, Rich); break;
            case "strike": ToggleStrike(); break;
            case "code": ToggleInlineCode(); break;
            case "p": SetHeading(0); break;
            case "h1": SetHeading(1); break;
            case "h2": SetHeading(2); break;
            case "h3": SetHeading(3); break;
            case "ul": ToggleList(TextMarkerStyle.Disc); break;
            case "ol": ToggleList(TextMarkerStyle.Decimal); break;
            case "task": ToggleTaskList(); break;
            case "quote": ToggleQuote(); break;
            case "codeblock": ToggleCodeBlock(); break;
            case "link": EditLink(); break;
            case "wiki": InsertWikiLink(); break;
            case "image": InsertImageFromDialog(); break;
            case "table": InsertTable(); break;
            case "rule": InsertRule(); break;
            case "undo": Rich.Undo(); break;
            case "redo": Rich.Redo(); break;
        }
    }

    private Paragraph? CaretParagraph => Rich.CaretPosition.Paragraph;

    private List<Paragraph> SelectedParagraphs()
    {
        var selection = Rich.Selection;
        var list = MarkdownFlow.Paragraphs(Rich.Document)
            .Where(p => p.ContentEnd.CompareTo(selection.Start) >= 0 && p.ContentStart.CompareTo(selection.End) <= 0)
            .ToList();
        if (list.Count == 0 && CaretParagraph is { } caret) list.Add(caret);
        return list;
    }

    private static bool IsEmptyParagraph(Paragraph p) =>
        new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length == 0 && !p.Inlines.OfType<InlineUIContainer>().Any();

    /// <summary>
    /// Double-clicking a word also selects the space after it: the selection is shrunk to the text,
    /// so that formats like inline code do not swallow the space.
    /// </summary>
    private void TrimSelection()
    {
        var selection = Rich.Selection;
        for (var guard = 0; guard < 8 && !selection.IsEmpty && selection.Text.EndsWith(' '); guard++)
        {
            var end = selection.End.GetNextInsertionPosition(LogicalDirection.Backward);
            if (end is null || end.CompareTo(selection.Start) <= 0) break;
            selection.Select(selection.Start, end);
        }
    }

    private void ToggleStrike()
    {
        TrimSelection();
        var selection = Rich.Selection;
        if (selection.IsEmpty) return;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current != null && current.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    private void ToggleInlineCode()
    {
        TrimSelection();
        var selection = Rich.Selection;
        if (selection.IsEmpty) return;
        var family = selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
        var isCode = family != null && family.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase);
        if (isCode)
        {
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
            selection.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        }
        else
        {
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, _style.Mono);
            selection.ApplyPropertyValue(TextElement.BackgroundProperty, _style.Chip);
        }
    }

    private void SetHeading(int level)
    {
        foreach (var p in SelectedParagraphs())
        {
            if (p.Tag as string == MarkdownFlow.TagFront) continue;
            if (level == 0)
            {
                MarkdownFlow.ApplyNormal(p);
                if (p.Parent is ListItem) p.Margin = new Thickness(0, 1, 0, 1);
            }
            else if (!MarkdownFlow.IsCode(p))
            {
                MarkdownFlow.ApplyHeading(p, level, _style);
            }
        }
    }

    private static void StyleList(List list, bool task)
    {
        list.Margin = new Thickness(0, 0, 0, 10);
        list.Padding = new Thickness(task && list.MarkerStyle == TextMarkerStyle.None ? 6 : 26, 0, 0, 0);
    }

    private static void RemoveCheckBoxes(List list)
    {
        foreach (var item in list.ListItems.ToList())
        {
            item.Tag = null;
            if (item.Blocks.FirstBlock is Paragraph p && MarkdownFlow.HasCheckBox(p)) p.Inlines.Remove(p.Inlines.FirstInline);
        }
    }

    private static bool IsNumbered(List list) =>
        list.MarkerStyle is not (TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None);

    private void ToggleList(TextMarkerStyle marker)
    {
        var list = MarkdownFlow.Ancestor<List>(CaretParagraph);
        if (list != null && list.Tag as string == MarkdownFlow.TagTask)
        {
            RemoveCheckBoxes(list);
            list.Tag = null;
            list.MarkerStyle = marker;
            StyleList(list, false);
            return;
        }
        if (marker == TextMarkerStyle.Disc) EditingCommands.ToggleBullets.Execute(null, Rich);
        else EditingCommands.ToggleNumbering.Execute(null, Rich);
        if (MarkdownFlow.Ancestor<List>(CaretParagraph) is { } created) StyleList(created, false);
    }

    private void ToggleTaskList()
    {
        var list = MarkdownFlow.Ancestor<List>(CaretParagraph);
        if (list != null && list.Tag as string == MarkdownFlow.TagTask)
        {
            RemoveCheckBoxes(list);
            list.Tag = null;
            list.MarkerStyle = TextMarkerStyle.Disc;
            StyleList(list, false);
            return;
        }
        if (list is null)
        {
            EditingCommands.ToggleBullets.Execute(null, Rich);
            list = MarkdownFlow.Ancestor<List>(CaretParagraph);
        }
        if (list is null) return;
        list.Tag = MarkdownFlow.TagTask;
        if (!IsNumbered(list)) list.MarkerStyle = TextMarkerStyle.None; // a numbered list keeps its numbers
        foreach (var item in list.ListItems) item.Tag = null;
        StyleList(list, true);
        NormalizeTaskLists();
    }

    private static Section? QuoteOf(TextElement? element)
    {
        DependencyObject? current = element;
        while (current is TextElement text)
        {
            if (text is Section { Tag: MarkdownFlow.TagQuote } section) return section;
            current = text.Parent;
        }
        return null;
    }

    private void ToggleQuote()
    {
        var document = Rich.Document;
        var caret = CaretParagraph;
        var quote = QuoteOf(caret);
        if (quote != null)
        {
            // Take the content out of the quote.
            var parent = MarkdownFlow.BlocksOf(quote.Parent);
            if (parent is null) return;
            foreach (var block in quote.Blocks.ToList())
            {
                quote.Blocks.Remove(block);
                parent.InsertBefore(quote, block);
            }
            parent.Remove(quote);
            if (caret != null) Rich.CaretPosition = caret.ContentEnd;
            return;
        }

        var first = MarkdownFlow.TopLevelBlock(document, Rich.Selection.Start.Paragraph) ?? MarkdownFlow.TopLevelBlock(document, CaretParagraph);
        var last = MarkdownFlow.TopLevelBlock(document, Rich.Selection.End.Paragraph) ?? first;
        if (first is null || last is null) return;

        var blocks = new List<Block>();
        for (var block = first; block != null; block = block.NextBlock)
        {
            blocks.Add(block);
            if (block == last) break;
        }
        var section = new Section();
        MarkdownFlow.ApplyQuote(section, _style);
        document.Blocks.InsertBefore(first, section);
        foreach (var block in blocks)
        {
            document.Blocks.Remove(block);
            section.Blocks.Add(block);
        }
        MarkdownFlow.EnsureTrailingParagraph(document);
        // Moving the paragraph took the caret out of it: put it back.
        if (caret != null) Rich.CaretPosition = caret.ContentEnd;
    }

    private void ToggleCodeBlock()
    {
        var caret = CaretParagraph;
        if (caret is null) return;
        if (MarkdownFlow.IsCode(caret))
        {
            MarkdownFlow.ApplyNormal(caret);
            return;
        }

        // The selected paragraphs of the same container become the lines of one code block.
        var paragraphs = SelectedParagraphs().Where(p => p.Parent == caret.Parent && p.Tag as string != MarkdownFlow.TagFront).ToList();
        if (!paragraphs.Contains(caret)) paragraphs = new List<Paragraph> { caret };
        var target = paragraphs[0];
        var text = string.Join("\n", paragraphs.Select(p => MarkdownFlow.PlainText(p.Inlines)));
        var owner = MarkdownFlow.BlocksOf(target.Parent);
        foreach (var extra in paragraphs.Skip(1)) owner?.Remove(extra);
        MarkdownFlow.ApplyCode(target, "", _style);
        MarkdownFlow.SetLines(target, text);
        Rich.CaretPosition = target.ContentEnd;
        MarkdownFlow.EnsureTrailingParagraph(Rich.Document);
    }

    /// <summary>A position where an inline can be inserted (creating a paragraph when the document has none).</summary>
    private TextPointer InsertionPoint()
    {
        if (!Rich.Selection.IsEmpty) Rich.Selection.Text = "";
        var position = Rich.CaretPosition;
        if (MarkdownFlow.Ancestor<Hyperlink>(position.Parent as TextElement) is { } inside) position = inside.ElementEnd;
        if (position.Paragraph is null)
        {
            var paragraph = MarkdownFlow.NewParagraph();
            Rich.Document.Blocks.Add(paragraph);
            position = paragraph.ContentStart;
        }
        return position.GetInsertionPosition(LogicalDirection.Forward);
    }

    private void EditLink()
    {
        if (Owner is null) return;
        var selection = Rich.Selection;
        var existing = MarkdownFlow.Ancestor<Hyperlink>(selection.Start.Parent as TextElement)
                       ?? MarkdownFlow.Ancestor<Hyperlink>(selection.End.Parent as TextElement);
        var info = MarkdownFlow.LinkOf(existing);
        var current = info is { IsWiki: false } ? info.Value.Href : "";
        if (info is { IsWiki: true })
        {
            InsertWikiLink();
            return;
        }

        var url = Owner.Dialogs.Prompt("Link", existing is null
            ? "Web address (https://…) or path of a file:"
            : "Web address or path. Leave it empty to remove the link:", current);
        if (url is null) return;

        if (existing != null)
        {
            if (url.Length == 0) Unlink(existing);
            else MarkdownFlow.StyleHyperlink(existing, url, false, _style, info?.Title);
            return;
        }
        if (url.Length == 0) return;

        if (selection.IsEmpty)
        {
            var link = new Hyperlink(new Run(url), InsertionPoint());
            StyleLink(link, url, false);
            Rich.CaretPosition = link.ElementEnd;
            return;
        }
        if (selection.Start.Paragraph is null || selection.Start.Paragraph != selection.End.Paragraph)
        {
            Owner.Dialogs.Info("Select the text of the link inside a single paragraph.", "Link");
            return;
        }
        StyleLink(new Hyperlink(selection.Start, selection.End), url, false);
    }

    private void StyleLink(Hyperlink link, string href, bool isWiki) =>
        MarkdownFlow.StyleHyperlink(link, href, isWiki, _style);

    private static void Unlink(Hyperlink link)
    {
        InlineCollection? parent = link.Parent switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            _ => null
        };
        if (parent is null) return;
        foreach (var inline in link.Inlines.ToList())
        {
            link.Inlines.Remove(inline);
            parent.InsertBefore(link, inline);
        }
        parent.Remove(link);
    }

    private void InsertWikiLink()
    {
        if (_vm is null || Owner is null) return;
        var target = Owner.PickLink(_vm);
        if (target is null) return;
        var label = Rich.Selection.IsEmpty ? target : Rich.Selection.Text.Trim();
        if (label.Length == 0 || label.Contains('\n')) label = target;
        var link = new Hyperlink(new Run(label), InsertionPoint());
        StyleLink(link, target, true);
        Rich.CaretPosition = link.ElementEnd;
    }

    private const string ImageFilter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";

    private void InsertImageFromDialog()
    {
        var file = Owner?.Dialogs.PickFile(ImageFilter);
        if (file != null) InsertImageFile(file);
    }

    /// <summary>Copies a picture into docs/.assets and returns its path relative to this document.</summary>
    private string? StoreImage(string file)
    {
        if (_vm is null || Owner is null) return null;
        try
        {
            return DocsService.RelativeLink(_vm.Node.DisplayPath, Owner.Service.ImportAsset(file));
        }
        catch (Exception ex)
        {
            Owner.Dialogs.Error("The picture could not be added.\n\n" + ex.Message);
            return null;
        }
    }

    private string? StoreImage(BitmapSource bitmap)
    {
        if (_vm is null || Owner is null) return null;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return DocsService.RelativeLink(_vm.Node.DisplayPath, Owner.Service.SaveAsset(stream.ToArray()));
        }
        catch (Exception ex)
        {
            Owner.Dialogs.Error("The picture could not be added.\n\n" + ex.Message);
            return null;
        }
    }

    private void InsertImageFile(string file)
    {
        var relative = StoreImage(file);
        if (relative != null) InsertImage(relative);
    }

    private void InsertImage(string relative)
    {
        if (_vm is null) return;
        if (_vm.IsPreview)
        {
            var container = MarkdownFlow.InsertImage(InsertionPoint(), relative, "", ResolveImage, _style);
            Rich.CaretPosition = container.ElementEnd;
            MarkPreviewDirty();
        }
        else
        {
            Source.SelectedText = $"![]({MdDoc.LinkTarget(relative)})";
            Source.CaretIndex = Source.SelectionStart + Source.SelectionLength;
        }
    }

    /// <summary>Adds a block after the block the caret is in (or in place of an empty paragraph).</summary>
    private void InsertBlock(Block block)
    {
        var document = Rich.Document;
        var current = MarkdownFlow.TopLevelBlock(document, CaretParagraph);
        if (current is null) document.Blocks.Add(block);
        else if (current is Paragraph { Tag: null } p && IsEmptyParagraph(p)) document.Blocks.InsertBefore(current, block);
        else document.Blocks.InsertAfter(current, block);

        if (block.NextBlock is null) document.Blocks.Add(MarkdownFlow.NewParagraph());
        if (block.NextBlock is Paragraph next) Rich.CaretPosition = next.ContentStart;
    }

    private void InsertTable()
    {
        var size = Owner?.Dialogs.Prompt("Table", "Columns x rows:", "3x2");
        if (string.IsNullOrWhiteSpace(size)) return;
        var match = Regex.Match(size, @"^\s*(\d+)\s*[xX×*,; ]\s*(\d+)\s*$");
        var columns = match.Success ? Math.Clamp(int.Parse(match.Groups[1].Value), 1, 12) : 3;
        var rows = match.Success ? Math.Clamp(int.Parse(match.Groups[2].Value), 1, 50) : 2;
        var table = MarkdownFlow.NewTable(columns, rows, _style);
        InsertBlock(table);
        var firstCell = table.RowGroups[0].Rows[Math.Min(1, table.RowGroups[0].Rows.Count - 1)].Cells[0];
        if (firstCell.Blocks.FirstBlock is Paragraph cellParagraph) Rich.CaretPosition = cellParagraph.ContentStart;
    }

    private void InsertRule() => InsertBlock(MarkdownFlow.NewRule(_style));

    // ---------------------------------------------------------------------------- keyboard & mouse (formatted view)

    // WPF inserts the typed characters of a RichTextBox a moment later (it batches fast typing at
    // "Background" priority). The keys handled below look at the text before the caret, so the
    // characters still waiting must be inserted first. WPF has a method for exactly that, but it is
    // internal: it is called through reflection, and if a future version drops it the only effect is
    // that a shortcut typed extremely fast might be missed.
    private static readonly System.Reflection.PropertyInfo? TextEditorProperty =
        typeof(System.Windows.Controls.Primitives.TextBoxBase).GetProperty("TextEditor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.MethodInfo? FlushTypingMethod =
        typeof(RichTextBox).Assembly.GetType("System.Windows.Documents.TextEditorTyping")?.GetMethod("_FlushPendingInputItems",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

    private void FlushTyping()
    {
        try
        {
            var editor = TextEditorProperty?.GetValue(Rich);
            if (editor != null) FlushTypingMethod?.Invoke(null, new[] { editor });
        }
        catch
        {
            // see the comment above: not critical
        }
    }

    private void Rich_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key is Key.Enter or Key.Space or Key.Tab or Key.Back) FlushTyping();
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (control && e.Key == Key.S)
        {
            _vm.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.K)
        {
            RunTool("link");
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.V && PasteFiles())
        {
            e.Handled = true;
            return;
        }
        if (_vm.IsPreviewLocked) return; // read-only: nothing below applies

        var p = CaretParagraph;
        if (p is null) return;
        var isFront = p.Tag as string == MarkdownFlow.TagFront;

        if (e.Key == Key.Enter && !shift)
        {
            if (p.Parent is ListItem { List.Tag: MarkdownFlow.TagTask } && MarkdownFlow.HasCheckBox(p) &&
                new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length == 0)
            {
                // Enter on an empty task: without its check box the item is really empty, and the
                // standard behaviour of lists applies (the list ends here).
                p.Inlines.Clear();
                MarkPreviewDirty();
                return;
            }
            if (MarkdownFlow.IsCode(p) || isFront)
            {
                if (control)
                {
                    // Ctrl+Enter leaves the block (code, or the front matter at the top of the file).
                    var after = MarkdownFlow.NewParagraph();
                    MarkdownFlow.BlocksOf(p.Parent)?.InsertAfter(p, after);
                    Rich.CaretPosition = after.ContentStart;
                }
                else
                {
                    EditingCommands.EnterLineBreak.Execute(null, Rich);
                }
                e.Handled = true;
            }
            else if (MarkdownFlow.IsHeading(p) && new TextRange(Rich.CaretPosition, p.ContentEnd).IsEmpty && Rich.Selection.IsEmpty)
            {
                // Enter at the end of a heading starts a normal paragraph.
                var after = MarkdownFlow.NewParagraph();
                MarkdownFlow.BlocksOf(p.Parent)?.InsertAfter(p, after);
                Rich.CaretPosition = after.ContentStart;
                e.Handled = true;
            }
            else if (p.Parent is Section { Tag: MarkdownFlow.TagQuote } quote && IsEmptyParagraph(p) && quote.Blocks.Count > 1)
            {
                // Enter on an empty line leaves the quote.
                quote.Blocks.Remove(p);
                MarkdownFlow.BlocksOf(quote.Parent)?.InsertAfter(quote, p);
                Rich.CaretPosition = p.ContentStart;
                e.Handled = true;
            }
            if (e.Handled) MarkPreviewDirty();
            return;
        }

        if (e.Key == Key.Tab && (MarkdownFlow.IsCode(p) || isFront) && !control)
        {
            Rich.Selection.Text = "    ";
            Rich.Selection.Select(Rich.Selection.End, Rich.Selection.End);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && MarkdownFlow.IsHeading(p) && Rich.Selection.IsEmpty &&
            new TextRange(p.ContentStart, Rich.CaretPosition).IsEmpty)
        {
            // Backspace at the start of a heading turns it back into text.
            MarkdownFlow.ApplyNormal(p);
            MarkPreviewDirty();
            e.Handled = true;
            return;
        }

        // Shift is often still down after typing "#" or ">": it does not matter for a space.
        if (e.Key == Key.Space && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None && TryMarkdownShortcut(p))
        {
            MarkPreviewDirty();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Typing markdown in the formatted view: "# ", "## ", "### ", "- ", "1. ", "> " and "[] " at the
    /// start of a paragraph apply the matching format.
    /// </summary>
    private bool TryMarkdownShortcut(Paragraph p)
    {
        if (p.Tag != null || !Rich.Selection.IsEmpty) return false;
        var typed = new TextRange(p.ContentStart, Rich.CaretPosition);
        var prefix = typed.Text;
        var inList = MarkdownFlow.Ancestor<List>(p) != null;
        var action = prefix switch
        {
            "#" => "h1",
            "##" => "h2",
            "###" => "h3",
            "-" or "*" when !inList => "ul",
            "1." when !inList => "ol",
            ">" when QuoteOf(p) is null => "quote",
            "[]" or "[ ]" => "task",
            _ => null
        };
        if (action is null) return false;
        typed.Text = "";
        if (action == "task" && MarkdownFlow.Ancestor<List>(p) is { } list && list.Tag as string == MarkdownFlow.TagTask) return true;
        try
        {
            ApplyRich(action);
        }
        catch
        {
            // the shortcut is a convenience: if the format cannot be applied here the text stays as typed
        }
        return true;
    }

    private void Rich_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || Owner is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var position = Rich.GetPositionFromPoint(e.GetPosition(Rich), true);
        if (MarkdownFlow.LinkOf(MarkdownFlow.Ancestor<Hyperlink>(position?.Parent as TextElement)) is not { } link) return;
        e.Handled = true;

        _syncTimer.Stop();
        PushPreview();
        Owner.FollowLink(_vm, link.Href, link.IsWiki);
    }

    // ---------------------------------------------------------------------------- paste & drop

    /// <summary>Only plain text and pictures are pasted: formatting from other programs cannot be stored as markdown.</summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.SourceDataObject;
        if (_vm is null || _vm.IsPreviewLocked)
        {
            e.CancelCommand();
            return;
        }
        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            // Inside a code block (or the front matter) the lines must stay in the same block.
            var paragraph = CaretParagraph;
            if (paragraph != null && (MarkdownFlow.IsCode(paragraph) || paragraph.Tag as string == MarkdownFlow.TagFront) &&
                data.GetData(DataFormats.UnicodeText) is string text && text.Contains('\n'))
            {
                e.CancelCommand();
                Dispatcher.BeginInvoke(new Action(() => InsertLines(text)));
                return;
            }
            e.FormatToApply = DataFormats.UnicodeText;
            return;
        }

        e.CancelCommand();
        if (e.IsDragDrop) return; // drops are handled by Rich_PreviewDrop
        BitmapSource? bitmap = null;
        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap)) bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource ?? Clipboard.GetImage();
        }
        catch
        {
            // the clipboard is busy: nothing is pasted
        }
        if (bitmap is null) return;
        var picture = bitmap;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (StoreImage(picture) is { } relative) InsertImage(relative);
        }));
    }

    /// <summary>Ctrl+V with files copied in Explorer (WPF does not offer them to a rich text box).</summary>
    private bool PasteFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList() || Clipboard.ContainsText()) return false;
            var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (files.Length == 0) return false;
            Dispatcher.BeginInvoke(new Action(() => AddFiles(files)));
            return true;
        }
        catch
        {
            return false; // the clipboard is busy
        }
    }

    /// <summary>Inserts text at the caret keeping it in the same paragraph: new lines become line breaks.</summary>
    private void InsertLines(string text)
    {
        if (!Rich.Selection.IsEmpty) Rich.Selection.Text = "";
        var position = Rich.CaretPosition;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) position = position.InsertLineBreak();
            if (lines[i].Length == 0) continue;
            var range = new TextRange(position, position) { Text = lines[i] };
            position = range.End;
        }
        Rich.CaretPosition = position;
        MarkPreviewDirty();
    }

    /// <summary>Files dropped or pasted on the text: pictures are inserted, other files are added to the documentation.</summary>
    private void AddFiles(string[] files)
    {
        if (_vm is null || Owner is null) return;
        var others = new List<string>();
        var canInsert = !_vm.IsPreview || !_vm.IsPreviewLocked;
        foreach (var file in files)
        {
            if (canInsert && DocsService.IsImageName(file) && File.Exists(file)) InsertImageFile(file);
            else others.Add(file);
        }
        if (others.Count > 0) Owner.Import(others, DocsService.ParentOf(_vm.Path));
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Rich_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var position = Rich.GetPositionFromPoint(e.GetPosition(Rich), true);
        if (position != null && _vm is { IsPreviewLocked: false }) Rich.CaretPosition = position;
        Rich.Focus();
        Dispatcher.BeginInvoke(new Action(() => AddFiles(files)));
    }

    private void Source_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var index = Source.GetCharacterIndexFromPoint(e.GetPosition(Source), true);
        if (index >= 0) Source.CaretIndex = index;
        Source.Focus();
        Dispatcher.BeginInvoke(new Action(() => AddFiles(files)));
    }

    // ============================================================================ markdown view (source text)

    private void Source_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var action = e.Key switch
        {
            Key.B => "bold",
            Key.I => "italic",
            Key.K => "link",
            _ => null
        };
        if (e.Key == Key.S)
        {
            _vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            // A screenshot pasted in the source becomes a picture of the document.
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return;
                if (Clipboard.GetImage() is { } bitmap && StoreImage(bitmap) is { } relative) InsertImage(relative);
                e.Handled = true;
            }
            catch
            {
                // the clipboard is busy: the normal paste goes on
            }
        }
        else if (action != null)
        {
            RunTool(action);
            e.Handled = true;
        }
    }

    private void ApplySource(string action)
    {
        Source.Focus();
        switch (action)
        {
            case "bold": Wrap("**"); break;
            case "italic": Wrap("_"); break;
            case "strike": Wrap("~~"); break;
            case "code": Wrap("`"); break;
            case "p": Lines(line => HeadingRx.Replace(line, "")); break;
            case "h1": Lines(line => "# " + HeadingRx.Replace(line, "")); break;
            case "h2": Lines(line => "## " + HeadingRx.Replace(line, "")); break;
            case "h3": Lines(line => "### " + HeadingRx.Replace(line, "")); break;
            case "ul": TogglePrefix("- ", ListRx); break;
            case "ol": TogglePrefix("1. ", ListRx); break;
            case "task": TogglePrefix("- [ ] ", ListRx); break;
            case "quote": TogglePrefix("> ", QuoteRx); break;
            case "codeblock": Lines(null, before: "```", after: "```"); break;
            case "link":
            {
                var url = Owner?.Dialogs.Prompt("Link", "Web address (https://…) or path of a file:");
                if (string.IsNullOrEmpty(url)) break;
                var text = Source.SelectedText.Length > 0 ? Source.SelectedText : url;
                Source.SelectedText = $"[{text}]({MdDoc.LinkTarget(url)})";
                break;
            }
            case "wiki":
            {
                if (_vm is null || Owner?.PickLink(_vm) is not { } target) break;
                var label = Source.SelectedText.Trim();
                Source.SelectedText = label.Length > 0 && label != target && !label.Contains('\n') ? $"[[{target}|{label}]]" : $"[[{target}]]";
                break;
            }
            case "image": InsertImageFromDialog(); break;
            case "table": InsertSourceBlock("| Column 1 | Column 2 | Column 3 |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |"); break;
            case "rule": InsertSourceBlock("---"); break;
            case "undo": Source.Undo(); break;
            case "redo": Source.Redo(); break;
        }
    }

    private static readonly Regex HeadingRx = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex ListRx = new(@"^\s*(?:[-+*]\s+\[[ xX]\]\s+|[-+*]\s+|\d+[.)]\s+)", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^\s{0,3}>\s?", RegexOptions.Compiled);

    /// <summary>Puts a marker around the selection (or removes it when it is already there).</summary>
    private void Wrap(string marker)
    {
        var selected = Source.SelectedText;
        var start = Source.SelectionStart;
        if (selected.Length >= 2 * marker.Length && selected.StartsWith(marker) && selected.EndsWith(marker))
        {
            Source.SelectedText = selected[marker.Length..^marker.Length];
        }
        else if (selected.Length == 0)
        {
            Source.SelectedText = marker + marker;
            Source.Select(start + marker.Length, 0);
        }
        else
        {
            // Spaces at the edges stay outside the markers, as markdown requires.
            var core = selected.Trim();
            var lead = selected[..(selected.Length - selected.TrimStart().Length)];
            var trail = selected[selected.TrimEnd().Length..];
            if (core.Length == 0) return;
            Source.SelectedText = lead + marker + core + marker + trail;
            Source.Select(start + lead.Length, core.Length + 2 * marker.Length);
        }
    }

    /// <summary>Rewrites the lines touched by the selection, optionally adding a line before and after them.</summary>
    private void Lines(Func<string, string>? transform, string? before = null, string? after = null)
    {
        var text = Source.Text;
        var start = Source.SelectionStart;
        var end = start + Source.SelectionLength;
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = text.IndexOf('\n', Math.Max(end - (end > start && end > 0 && text[end - 1] == '\n' ? 1 : 0), lineStart));
        if (lineEnd < 0) lineEnd = text.Length;

        var lines = text[lineStart..lineEnd].Split('\n').ToList();
        if (transform != null)
        {
            // Empty lines inside a multi-line selection are left alone.
            var many = lines.Count > 1;
            lines = lines.Select(line => many && line.Trim().Length == 0 ? line : transform(line)).ToList();
        }
        if (before != null) lines.Insert(0, before);
        if (after != null) lines.Add(after);
        var replacement = string.Join("\n", lines);

        Source.Select(lineStart, lineEnd - lineStart);
        Source.SelectedText = replacement;
        Source.Select(lineStart, replacement.Length);
    }

    private void TogglePrefix(string prefix, Regex existing)
    {
        var text = Source.Text;
        var start = Source.SelectionStart;
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var firstLineEnd = text.IndexOf('\n', lineStart);
        var firstLine = text[lineStart..(firstLineEnd < 0 ? text.Length : firstLineEnd)];
        var remove = firstLine.TrimStart().StartsWith(prefix.TrimEnd()) && prefix != "1. " ||
                     prefix == "1. " && Regex.IsMatch(firstLine, @"^\s*\d+[.)]\s");
        var counter = 0;
        Lines(line =>
        {
            var bare = existing.Replace(line, "");
            if (remove) return bare;
            return (prefix == "1. " ? $"{++counter}. " : prefix) + bare;
        });
    }

    private void InsertSourceBlock(string block)
    {
        var text = Source.Text;
        var caret = Source.SelectionStart + Source.SelectionLength;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;
        var needsBlank = lineEnd > 0 && text[..lineEnd].TrimEnd(' ').Length > 0 && !text[..lineEnd].EndsWith("\n\n");
        var insert = (lineEnd > 0 && text[lineEnd - 1] != '\n' ? "\n" : "") + (needsBlank ? "\n" : "") + block + "\n";
        Source.Select(lineEnd, 0);
        Source.SelectedText = insert;
        Source.CaretIndex = lineEnd + insert.Length;
    }
}
