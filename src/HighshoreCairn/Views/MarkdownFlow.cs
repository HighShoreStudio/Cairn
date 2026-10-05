using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HighshoreCairn.Helpers;
using HighshoreCairn.Services;

namespace HighshoreCairn.Views;

/// <summary>Colors and fonts used to draw a markdown document (taken from the current theme).</summary>
public sealed class FlowStyle
{
    public Brush Text { get; init; } = Brushes.Black;
    public Brush Muted { get; init; } = Brushes.Gray;
    public Brush Link { get; init; } = Brushes.SteelBlue;
    public Brush Chip { get; init; } = Brushes.Gainsboro;
    public Brush Border { get; init; } = Brushes.LightGray;
    public Brush Primary { get; init; } = Brushes.SteelBlue;
    public FontFamily Mono { get; } = new("Consolas, Courier New");
    public double FontSize { get; init; } = 14;

    public static FlowStyle From(FrameworkElement element, double fontSize = 14)
    {
        Brush Find(string key, Brush fallback) => element.TryFindResource(key) as Brush ?? fallback;
        return new FlowStyle
        {
            Text = Find("Brush.Text", Brushes.Black),
            Muted = Find("Brush.TextMuted", Brushes.Gray),
            Link = Find("Brush.Link", Brushes.SteelBlue),
            Chip = Find("Brush.Chip", Brushes.Gainsboro),
            Border = Find("Brush.Border", Brushes.LightGray),
            Primary = Find("Brush.Primary", Brushes.SteelBlue),
            FontSize = fontSize
        };
    }
}

/// <summary>
/// Bridge between the markdown model (<see cref="MdBlock"/>) and WPF rich text: builds the FlowDocument
/// shown by the formatted view and reads it back after the user edited it.
/// What a block is (heading, code, quote...) is stored in its Tag, so reading does not depend on looks.
/// </summary>
public static class MarkdownFlow
{
    public const string TagCode = "code";
    public const string TagQuote = "quote";
    public const string TagRule = "hr";
    public const string TagTask = "task";
    /// <summary>Item without a check box inside a task list.</summary>
    public const string TagNoBox = "nobox";
    public const string TagFront = "front";
    public const string TagImage = "img:";
    public const string TagWiki = "wiki:";
    public const string TagUrl = "url:";

    private static readonly double[] HeadingScale = { 1.9, 1.55, 1.3, 1.15, 1.0, 0.92 };

    // ============================================================================ build

    public static FlowDocument Build(IEnumerable<MdBlock> blocks, FlowStyle style, Func<string, string?>? resolveImage = null, Action? changed = null)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = style.FontSize,
            Foreground = style.Text,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        // Paragraphs and lists created while typing get the same spacing as the ones built here
        // (the WPF default is a full empty line around every paragraph).
        var paragraphStyle = new Style(typeof(Paragraph));
        paragraphStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 10)));
        document.Resources.Add(typeof(Paragraph), paragraphStyle);
        var listStyle = new Style(typeof(List));
        listStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 10)));
        listStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(26, 0, 0, 0)));
        document.Resources.Add(typeof(List), listStyle);

        var context = new BuildContext(style, resolveImage, changed);
        foreach (var block in blocks) document.Blocks.Add(BuildBlock(block, context));
        EnsureTrailingParagraph(document);
        return document;
    }

    private sealed record BuildContext(FlowStyle Style, Func<string, string?>? ResolveImage, Action? Changed);

    /// <summary>An empty paragraph at the end lets the user click below a table, a code block or a list.</summary>
    public static void EnsureTrailingParagraph(FlowDocument document)
    {
        if (document.Blocks.LastBlock is Paragraph { Tag: null }) return;
        document.Blocks.Add(NewParagraph());
    }

    public static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 10) };

    private static Block BuildBlock(MdBlock block, BuildContext c)
    {
        switch (block)
        {
            case MdHeading heading:
            {
                var p = new Paragraph();
                ApplyHeading(p, heading.Level, c.Style);
                AddInlines(p.Inlines, heading.Inlines, c);
                return p;
            }
            case MdParagraph paragraph:
            {
                var p = NewParagraph();
                AddInlines(p.Inlines, paragraph.Inlines, c);
                return p;
            }
            case MdCodeBlock code:
            {
                var p = new Paragraph();
                ApplyCode(p, code.Language, c.Style);
                SetLines(p, code.Code);
                return p;
            }
            case MdFrontMatter front:
            {
                var p = new Paragraph
                {
                    Tag = TagFront, FontFamily = c.Style.Mono, FontSize = c.Style.FontSize * 0.9, Foreground = c.Style.Muted,
                    Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(10, 6, 10, 6),
                    BorderBrush = c.Style.Border, BorderThickness = new Thickness(1)
                };
                SetLines(p, front.Raw);
                return p;
            }
            case MdQuote quote:
            {
                var section = new Section();
                ApplyQuote(section, c.Style);
                foreach (var inner in quote.Blocks) section.Blocks.Add(BuildBlock(inner, c));
                if (section.Blocks.Count == 0) section.Blocks.Add(NewParagraph());
                return section;
            }
            case MdList list:
            {
                var isTask = list.Items.Count > 0 && list.Items.Any(i => i.Checked != null);
                var flowList = new List
                {
                    // A numbered task list keeps its numbers next to the check boxes.
                    MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : isTask ? TextMarkerStyle.None : TextMarkerStyle.Disc,
                    StartIndex = Math.Max(1, list.Start),
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(isTask && !list.Ordered ? 6 : 26, 0, 0, 0),
                    Tag = isTask ? TagTask : null
                };
                foreach (var item in list.Items)
                {
                    var listItem = new ListItem();
                    foreach (var inner in item.Blocks)
                    {
                        var built = BuildBlock(inner, c);
                        if (built is Paragraph p && p.Tag is null) p.Margin = new Thickness(0, 1, 0, 1);
                        else built.Margin = new Thickness(0, 2, 0, 2);
                        listItem.Blocks.Add(built);
                    }
                    var hasBox = item.Checked != null;
                    if (listItem.Blocks.Count == 0)
                        listItem.Blocks.Add(new Paragraph { Margin = new Thickness(0, 1, 0, 1) });
                    else if (hasBox && listItem.Blocks.FirstBlock is not Paragraph { Tag: null })
                        listItem.Blocks.InsertBefore(listItem.Blocks.FirstBlock, new Paragraph { Margin = new Thickness(0, 1, 0, 1) }); // holds the check box
                    if (hasBox) AddCheckBox((Paragraph)listItem.Blocks.FirstBlock!, item.Checked == true, c.Changed);
                    else if (isTask) listItem.Tag = TagNoBox; // a plain item among tasks stays plain
                    flowList.ListItems.Add(listItem);
                }
                if (flowList.ListItems.Count == 0) flowList.ListItems.Add(new ListItem(new Paragraph()));
                return flowList;
            }
            case MdRule:
                return NewRule(c.Style);
            case MdTable table:
                return BuildTable(table, c);
            default:
                return NewParagraph();
        }
    }

    public static BlockUIContainer NewRule(FlowStyle style) => new(new Border
    {
        Height = 1, Background = style.Border, Margin = new Thickness(0, 6, 0, 6), SnapsToDevicePixels = true
    })
    {
        Tag = TagRule, Margin = new Thickness(0, 0, 0, 10)
    };

    public static void ApplyHeading(Paragraph p, int level, FlowStyle style)
    {
        level = Math.Clamp(level, 1, 6);
        p.Tag = "h" + level;
        p.FontSize = style.FontSize * HeadingScale[level - 1];
        p.FontWeight = FontWeights.SemiBold;
        p.FontFamily = new FontFamily("Segoe UI");
        p.ClearValue(TextElement.BackgroundProperty);
        p.Padding = new Thickness(0, 0, 0, level <= 2 ? 3 : 0);
        p.Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 8);
        p.BorderBrush = style.Border;
        p.BorderThickness = new Thickness(0, 0, 0, level == 1 ? 1 : 0);
    }

    /// <summary>Back to a normal paragraph.</summary>
    public static void ApplyNormal(Paragraph p)
    {
        p.Tag = null;
        p.ClearValue(TextElement.FontSizeProperty);
        p.ClearValue(TextElement.FontWeightProperty);
        p.ClearValue(TextElement.FontFamilyProperty);
        p.ClearValue(TextElement.BackgroundProperty);
        p.ClearValue(Block.BorderBrushProperty);
        p.ClearValue(Block.BorderThicknessProperty);
        p.ClearValue(Block.PaddingProperty);
        p.Margin = new Thickness(0, 0, 0, 10);
    }

    public static void ApplyCode(Paragraph p, string language, FlowStyle style)
    {
        p.Tag = string.IsNullOrWhiteSpace(language) ? TagCode : TagCode + ":" + language.Trim();
        p.FontFamily = style.Mono;
        p.FontSize = style.FontSize * 0.93;
        p.ClearValue(TextElement.FontWeightProperty);
        p.Background = style.Chip;
        p.BorderBrush = style.Border;
        p.BorderThickness = new Thickness(1);
        p.Padding = new Thickness(10, 8, 10, 8);
        p.Margin = new Thickness(0, 0, 0, 10);
    }

    public static void ApplyQuote(Section section, FlowStyle style)
    {
        section.Tag = TagQuote;
        section.BorderBrush = style.Primary;
        section.BorderThickness = new Thickness(3, 0, 0, 0);
        section.Padding = new Thickness(12, 2, 0, 0);
        section.Margin = new Thickness(0, 0, 0, 10);
        section.Foreground = style.Muted;
    }

    /// <summary>Fills a paragraph with plain lines (code blocks).</summary>
    public static void SetLines(Paragraph p, string text)
    {
        p.Inlines.Clear();
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
        }
    }

    public static void AddCheckBox(Paragraph paragraph, bool isChecked, Action? changed)
    {
        var box = new CheckBox { IsChecked = isChecked, Margin = new Thickness(0, 0, 7, 0), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand };
        if (changed != null)
        {
            box.Checked += (_, _) => changed();
            box.Unchecked += (_, _) => changed();
        }
        var container = new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center, Tag = TagTask };
        if (paragraph.Inlines.FirstInline is null) paragraph.Inlines.Add(container);
        else paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, container);
    }

    public static bool HasCheckBox(Paragraph? paragraph) =>
        paragraph?.Inlines.FirstInline is InlineUIContainer { Child: CheckBox };

    private static Table BuildTable(MdTable table, BuildContext c)
    {
        var flow = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10), BorderBrush = c.Style.Border, BorderThickness = new Thickness(1, 1, 0, 0) };
        var columns = Math.Max(1, Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count)));
        for (var i = 0; i < columns; i++) flow.Columns.Add(new TableColumn());
        flow.Tag = string.Join(",", Enumerable.Range(0, columns).Select(i => (i < table.Align.Count ? table.Align[i] : MdAlign.None).ToString()));

        var group = new TableRowGroup();
        flow.RowGroups.Add(group);
        group.Rows.Add(BuildRow(table.Header, columns, table.Align, header: true, c));
        foreach (var row in table.Rows) group.Rows.Add(BuildRow(row, columns, table.Align, header: false, c));
        return flow;
    }

    private static TableRow BuildRow(List<List<MdInline>> cells, int columns, List<MdAlign> align, bool header, BuildContext c)
    {
        var row = new TableRow();
        for (var i = 0; i < columns; i++)
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            if (header) p.FontWeight = FontWeights.SemiBold;
            p.TextAlignment = (i < align.Count ? align[i] : MdAlign.None) switch
            {
                MdAlign.Center => TextAlignment.Center,
                MdAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            };
            if (i < cells.Count) AddInlines(p.Inlines, cells[i], c);
            var cell = new TableCell(p) { BorderBrush = c.Style.Border, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4, 8, 4) };
            if (header) cell.Background = c.Style.Chip;
            row.Cells.Add(cell);
        }
        return row;
    }

    public static Table NewTable(int columns, int rows, FlowStyle style)
    {
        var table = new MdTable();
        for (var i = 0; i < columns; i++)
        {
            table.Align.Add(MdAlign.None);
            table.Header.Add(new List<MdInline> { MdInline.Plain("Column " + (i + 1)) });
        }
        for (var r = 0; r < rows; r++) table.Rows.Add(Enumerable.Range(0, columns).Select(_ => new List<MdInline>()).ToList());
        return BuildTable(table, new BuildContext(style, null, null));
    }

    private static void AddInlines(InlineCollection target, IEnumerable<MdInline> runs, BuildContext c)
    {
        Hyperlink? link = null;
        string? linkKey = null;
        foreach (var run in MdDoc.Normalize(runs))
        {
            var key = run.Href is null || run.Kind == MdInlineKind.LineBreak ? null : (run.IsWiki ? TagWiki : TagUrl) + run.Href + "\n" + run.Title;
            if (key != linkKey)
            {
                link = key is null ? null : NewHyperlink(run.Href!, run.IsWiki, c.Style, run.Title);
                if (link != null) target.Add(link);
                linkKey = key;
            }
            var inlines = link?.Inlines ?? target;

            switch (run.Kind)
            {
                case MdInlineKind.LineBreak:
                    target.Add(new LineBreak());
                    break;
                case MdInlineKind.Image:
                    inlines.Add(NewImage(run.Source ?? "", run.Text, c.ResolveImage, c.Style, run.Title));
                    break;
                default:
                    var text = new Run(run.Text);
                    if (run.Bold) text.FontWeight = FontWeights.Bold;
                    if (run.Italic) text.FontStyle = FontStyles.Italic;
                    if (run.Strike) text.TextDecorations = TextDecorations.Strikethrough;
                    if (run.Code) ApplyInlineCode(text, c.Style);
                    inlines.Add(text);
                    break;
            }
        }
    }

    public static void ApplyInlineCode(TextElement element, FlowStyle style)
    {
        element.FontFamily = style.Mono;
        element.Background = style.Chip;
    }

    public static Hyperlink NewHyperlink(string href, bool isWiki, FlowStyle style, string? title = null)
    {
        var link = new Hyperlink();
        StyleHyperlink(link, href, isWiki, style, title);
        return link;
    }

    /// <summary>Stores the target of a link in its Tag ("url:target" or "wiki:target", plus an optional title line).</summary>
    public static void StyleHyperlink(Hyperlink link, string href, bool isWiki, FlowStyle style, string? title = null)
    {
        link.Tag = (isWiki ? TagWiki : TagUrl) + href + (string.IsNullOrEmpty(title) ? "" : "\n" + title);
        link.Foreground = style.Link;
        link.ToolTip = (string.IsNullOrEmpty(title) ? "" : title + "\n") + (isWiki ? "Document: " : "") + href + "\nCtrl+Click to open";
        link.Cursor = System.Windows.Input.Cursors.Hand;
    }

    /// <summary>The target stored in a link by <see cref="StyleHyperlink"/> (null for foreign hyperlinks without one).</summary>
    public static (string Href, bool IsWiki, string? Title)? LinkOf(Hyperlink? link)
    {
        if (link is null) return null;
        if (link.Tag is string tag)
        {
            var isWiki = tag.StartsWith(TagWiki);
            if (isWiki || tag.StartsWith(TagUrl))
            {
                var parts = tag[(isWiki ? TagWiki.Length : TagUrl.Length)..].Split('\n', 2);
                return (parts[0], isWiki, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
            }
        }
        var uri = link.NavigateUri?.OriginalString;
        return string.IsNullOrEmpty(uri) ? null : (uri, false, null);
    }

    /// <summary>The element that shows a picture (or a small placeholder when the file is missing).</summary>
    private static UIElement ImageElement(string source, string alt, Func<string, string?>? resolve, FlowStyle style)
    {
        var file = resolve?.Invoke(source);
        ImageSource? picture = null;
        if (file != null && !MdDoc.IsUrl(file)) picture = ImageCodec.FromFile(file);
        if (picture != null)
        {
            var width = picture is System.Windows.Media.Imaging.BitmapSource bitmap ? bitmap.PixelWidth : 600;
            var image = new Image { Source = picture, Stretch = Stretch.Uniform, MaxWidth = Math.Min(720, Math.Max(16, width)), ToolTip = string.IsNullOrEmpty(alt) ? source : alt };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        return new Border
        {
            BorderBrush = style.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock { Text = "[picture] " + (string.IsNullOrEmpty(alt) ? source : alt), Foreground = style.Muted, FontSize = style.FontSize * 0.9 },
            ToolTip = MdDoc.IsUrl(source) ? "Picture on the web: " + source : "Picture not found: " + source
        };
    }

    public static InlineUIContainer NewImage(string source, string alt, Func<string, string?>? resolve, FlowStyle style, string? title = null) =>
        new(ImageElement(source, alt, resolve, style)) { Tag = ImageTag(source, alt, title), BaselineAlignment = BaselineAlignment.Bottom };

    /// <summary>Inserts a picture at a position of the text.</summary>
    public static InlineUIContainer InsertImage(TextPointer position, string source, string alt, Func<string, string?>? resolve, FlowStyle style) =>
        new(ImageElement(source, alt, resolve, style), position) { Tag = ImageTag(source, alt, null), BaselineAlignment = BaselineAlignment.Bottom };

    private static string ImageTag(string source, string alt, string? title) =>
        TagImage + source + "\n" + alt.Replace('\n', ' ') + "\n" + (title ?? "");

    /// <summary>Every list of the document, also the nested ones.</summary>
    public static IEnumerable<List> Lists(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case List list:
                    yield return list;
                    foreach (var item in list.ListItems)
                    foreach (var inner in Lists(item.Blocks)) yield return inner;
                    break;
                case Section section:
                    foreach (var inner in Lists(section.Blocks)) yield return inner;
                    break;
            }
        }
    }

    /// <summary>The block collection that holds the children of a container (document, quote, list item, table cell).</summary>
    public static BlockCollection? BlocksOf(DependencyObject? parent) => parent switch
    {
        FlowDocument document => document.Blocks,
        Section section => section.Blocks,
        ListItem item => item.Blocks,
        TableCell cell => cell.Blocks,
        _ => null
    };

    // ============================================================================ read

    /// <summary>Reads the document back into markdown blocks (after the user edited the formatted view).</summary>
    public static List<MdBlock> Read(FlowDocument document)
    {
        var blocks = new List<MdBlock>();
        ReadBlocks(document.Blocks, blocks);
        return blocks;
    }

    private static void ReadBlocks(BlockCollection source, List<MdBlock> output)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case Paragraph p:
                    ReadParagraph(p, output);
                    break;

                case Section section:
                    if (section.Tag as string == TagQuote)
                    {
                        var quote = new MdQuote();
                        ReadBlocks(section.Blocks, quote.Blocks);
                        if (quote.Blocks.Count > 0) output.Add(quote);
                    }
                    else
                    {
                        ReadBlocks(section.Blocks, output);
                    }
                    break;

                case List list:
                {
                    var isTask = list.Tag as string == TagTask;
                    var md = new MdList
                    {
                        Ordered = list.MarkerStyle is not (TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None),
                        Start = Math.Max(1, list.StartIndex)
                    };
                    foreach (var item in list.ListItems)
                    {
                        var mdItem = new MdListItem();
                        var first = item.Blocks.FirstBlock as Paragraph;
                        if (HasCheckBox(first))
                            mdItem.Checked = first?.Inlines.FirstInline is InlineUIContainer { Child: CheckBox { IsChecked: true } };
                        else if (isTask && item.Tag as string != TagNoBox)
                            mdItem.Checked = false; // a new task whose box was not added yet
                        ReadBlocks(item.Blocks, mdItem.Blocks);
                        md.Items.Add(mdItem);
                    }
                    // Empty items at the end are the "next bullet" the user has not typed yet.
                    while (md.Items.Count > 1 && md.Items[^1].Blocks.Count == 0) md.Items.RemoveAt(md.Items.Count - 1);
                    if (md.Items.Count > 0 && !(md.Items.Count == 1 && md.Items[0].Blocks.Count == 0)) output.Add(md);
                    break;
                }

                case Table table:
                    ReadTable(table, output);
                    break;

                case BlockUIContainer container:
                    if (container.Tag as string == TagRule) output.Add(new MdRule());
                    break;
            }
        }
    }

    private static void ReadParagraph(Paragraph p, List<MdBlock> output)
    {
        var tag = p.Tag as string ?? "";
        if (tag == TagCode || tag.StartsWith(TagCode + ":"))
        {
            output.Add(new MdCodeBlock { Language = tag.Length > TagCode.Length ? tag[(TagCode.Length + 1)..] : "", Code = PlainText(p.Inlines) });
            return;
        }
        if (tag == TagFront)
        {
            output.Add(new MdFrontMatter { Raw = PlainText(p.Inlines) });
            return;
        }

        var baseBold = p.FontWeight.ToOpenTypeWeight() >= 600;
        var inlines = new List<MdInline>();
        ReadInlines(p.Inlines, inlines, null, false, false, baseBold);
        var normalized = MdDoc.Normalize(inlines);

        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            // A heading is one line: line breaks become spaces.
            foreach (var run in normalized.Where(r => r.Kind == MdInlineKind.LineBreak).ToList())
                normalized[normalized.IndexOf(run)] = MdInline.Plain(" ");
            output.Add(new MdHeading { Level = tag[1] - '0', Inlines = normalized });
            return;
        }
        if (normalized.Count > 0) output.Add(new MdParagraph { Inlines = normalized });
    }

    private static void ReadTable(Table table, List<MdBlock> output)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        if (rows.Count == 0) return;
        var md = new MdTable();
        var columns = rows.Max(r => r.Cells.Count);
        var aligns = (table.Tag as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < columns; i++)
            md.Align.Add(i < aligns.Length && Enum.TryParse<MdAlign>(aligns[i], out var a) ? a : MdAlign.None);

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new List<List<MdInline>>();
            foreach (var cell in rows[r].Cells)
            {
                var inlines = new List<MdInline>();
                foreach (var block in cell.Blocks)
                {
                    if (block is not Paragraph p) continue;
                    if (inlines.Count > 0) inlines.Add(MdInline.Break());
                    ReadInlines(p.Inlines, inlines, null, false, false, p.FontWeight.ToOpenTypeWeight() >= 600);
                }
                cells.Add(MdDoc.Normalize(inlines));
            }
            while (cells.Count < columns) cells.Add(new List<MdInline>());
            if (r == 0) md.Header = cells;
            else md.Rows.Add(cells);
        }
        output.Add(md);
    }

    private static bool IsMono(FontFamily family) =>
        family.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || family.Source.Contains("Courier", StringComparison.OrdinalIgnoreCase);

    private static bool HasStrike(TextDecorationCollection? decorations) =>
        decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);

    private static void ReadInlines(InlineCollection source, List<MdInline> output, string? href, bool isWiki, bool strike, bool baseBold)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case Run run:
                    if (run.Text.Length == 0) break;
                    output.Add(new MdInline
                    {
                        Text = run.Text,
                        // In headings and table headers (already semi-bold) only a heavier weight counts as bold.
                        Bold = run.FontWeight.ToOpenTypeWeight() >= (baseBold ? 700 : 600),
                        Italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique,
                        Strike = strike || HasStrike(run.TextDecorations),
                        Code = IsMono(run.FontFamily),
                        Href = href,
                        IsWiki = isWiki
                    });
                    break;

                case LineBreak:
                    output.Add(MdInline.Break());
                    break;

                case Hyperlink link:
                {
                    var info = LinkOf(link);
                    var before = output.Count;
                    ReadInlines(link.Inlines, output, info?.Href ?? href, info?.IsWiki ?? false, strike || HasStrike(link.TextDecorations), baseBold);
                    if (info?.Title != null)
                        for (var k = before; k < output.Count; k++)
                            if (output[k].Kind == MdInlineKind.Text) output[k].Title = info.Value.Title;
                    break;
                }

                case Span span:
                    ReadInlines(span.Inlines, output, href, isWiki, strike || HasStrike(span.TextDecorations), baseBold);
                    break;

                case InlineUIContainer container:
                    if (container.Tag is string imageTag && imageTag.StartsWith(TagImage))
                    {
                        var parts = imageTag[TagImage.Length..].Split('\n', 3);
                        output.Add(new MdInline
                        {
                            Kind = MdInlineKind.Image, Source = parts[0], Text = parts.Length > 1 ? parts[1] : "", Href = href,
                            Title = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null
                        });
                    }
                    break;
            }
        }
    }

    /// <summary>The text of some inlines, with a new line for every line break.</summary>
    public static string PlainText(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        void Walk(InlineCollection list)
        {
            foreach (var inline in list)
            {
                switch (inline)
                {
                    case Run run: sb.Append(run.Text); break;
                    case LineBreak: sb.Append('\n'); break;
                    case Span span: Walk(span.Inlines); break;
                }
            }
        }
        Walk(inlines);
        return sb.ToString();
    }

    // ============================================================================ navigation helpers

    /// <summary>Every paragraph of the document, in reading order (also inside lists, quotes and tables).</summary>
    public static IEnumerable<Paragraph> Paragraphs(FlowDocument document) => Paragraphs(document.Blocks);

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case Section section:
                    foreach (var p in Paragraphs(section.Blocks)) yield return p;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var p in Paragraphs(item.Blocks)) yield return p;
                    break;
                case Table table:
                    foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                    foreach (var p in Paragraphs(cell.Blocks)) yield return p;
                    break;
            }
        }
    }

    public static bool IsHeading(Paragraph p) => p.Tag is string { Length: 2 } tag && tag[0] == 'h';
    public static bool IsCode(Paragraph p) => p.Tag is string tag && (tag == TagCode || tag.StartsWith(TagCode + ":"));

    /// <summary>The top-level headings, in the same order as <see cref="MdDoc.Outline"/>.</summary>
    public static List<Paragraph> TopLevelHeadings(FlowDocument document) =>
        document.Blocks.OfType<Paragraph>().Where(IsHeading).ToList();

    /// <summary>The block of the document (direct child) that contains an element.</summary>
    public static Block? TopLevelBlock(FlowDocument document, TextElement? element)
    {
        DependencyObject? current = element;
        while (current is TextElement text)
        {
            if (text.Parent is FlowDocument) return text as Block;
            current = text.Parent;
        }
        return null;
    }

    public static T? Ancestor<T>(TextElement? element) where T : TextElement
    {
        DependencyObject? current = element;
        while (current is TextElement text)
        {
            if (text is T match) return match;
            current = text.Parent;
        }
        return null;
    }
}
