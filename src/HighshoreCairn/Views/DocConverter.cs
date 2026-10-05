using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Converts documents between markdown and rich text (RTF). Both directions go through a WPF
/// FlowDocument: markdown is built with <see cref="MarkdownFlow"/>, RTF is read and written by WPF.
/// What the target format cannot hold is dropped: colors, fonts, sizes, underline and alignment when
/// going to markdown; check boxes (they become ☐ / ☑) and links between documents when going to RTF.
/// </summary>
public class DocConverter : IDocConverter
{
    // A rich text document is a white page with black text, whatever the theme of the app.
    private static readonly FlowStyle Paper = new()
    {
        Text = Brushes.Black,
        Muted = Frozen("#555F6B"),
        Link = Frozen("#1F6F8B"),
        Chip = Frozen("#EEF1F4"),
        Border = Frozen("#C3CBD3"),
        Primary = Frozen("#1F6F8B"),
        FontSize = 16
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ============================================================================ markdown -> RTF

    public string MarkdownToRtf(string markdown, Func<string, string?> resolveImage)
    {
        var document = MarkdownFlow.Build(MdDoc.Parse(markdown ?? ""), Paper, resolveImage);
        Flatten(document.Blocks);
        // The empty paragraph added for the editor is not part of the text.
        if (document.Blocks.Count > 1 && document.Blocks.LastBlock is Paragraph { Tag: null } last && last.Inlines.Count == 0)
            document.Blocks.Remove(last);

        using var stream = new MemoryStream();
        new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Rtf);
        return RtfText.FromBytes(stream.ToArray());
    }

    /// <summary>Replaces what RTF cannot store (check boxes, rules, placeholders, links without an address).</summary>
    private static void Flatten(BlockCollection blocks)
    {
        foreach (var block in blocks.ToList())
        {
            switch (block)
            {
                case Paragraph paragraph:
                    // RTF only knows bold / not bold: the semi-bold of headings and table headers would be lost.
                    if (paragraph.FontWeight.ToOpenTypeWeight() is >= 600 and < 700) paragraph.FontWeight = FontWeights.Bold;
                    Flatten(paragraph.Inlines);
                    break;
                case Section section:
                    Flatten(section.Blocks);
                    break;
                case List list:
                    // (copies: any change in the document invalidates the enumerators of its collections)
                    foreach (var item in list.ListItems.ToList()) Flatten(item.Blocks);
                    break;
                case Table table:
                    foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).ToList()) Flatten(cell.Blocks);
                    break;
                case BlockUIContainer container when container.Tag as string == MarkdownFlow.TagRule:
                {
                    // A horizontal line: an empty paragraph with a bottom border.
                    var rule = new Paragraph
                    {
                        BorderBrush = Paper.Border, BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 4, 0, 12), FontSize = 4
                    };
                    blocks.InsertBefore(container, rule);
                    blocks.Remove(container);
                    break;
                }
            }
        }
    }

    private static void Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToList())
        {
            switch (inline)
            {
                case InlineUIContainer { Child: CheckBox box } container:
                    inlines.InsertBefore(container, new Run(box.IsChecked == true ? "☑ " : "☐ "));
                    inlines.Remove(container);
                    break;

                case InlineUIContainer { Child: Image } container:
                    container.Tag = null;   // a picture: WPF stores it inside the RTF
                    break;

                case InlineUIContainer container:
                {
                    // The placeholder of a picture that was not found.
                    var text = container.Tag is string tag && tag.StartsWith(MarkdownFlow.TagImage)
                        ? tag[MarkdownFlow.TagImage.Length..].Split('\n')[0] : "";
                    inlines.InsertBefore(container, new Run($"[picture: {text}]") { Foreground = Paper.Muted });
                    inlines.Remove(container);
                    break;
                }

                case Hyperlink link:
                {
                    Flatten(link.Inlines);
                    var info = MarkdownFlow.LinkOf(link);
                    link.Tag = null;
                    link.ToolTip = null;
                    if (info is { IsWiki: false } && MdDoc.IsUrl(info.Value.Href) && Uri.TryCreate(info.Value.Href, UriKind.Absolute, out var uri))
                    {
                        link.NavigateUri = uri;   // a web link survives as a real hyperlink
                    }
                    else
                    {
                        // A link to another document has no address outside the app: keep its text.
                        var span = new Span { Foreground = Paper.Link };
                        foreach (var child in link.Inlines.ToList())
                        {
                            link.Inlines.Remove(child);
                            span.Inlines.Add(child);
                        }
                        inlines.InsertBefore(link, span);
                        inlines.Remove(link);
                    }
                    break;
                }

                case Span span:
                    Flatten(span.Inlines);
                    break;
            }
        }
    }

    // ============================================================================ RTF -> markdown

    public string RtfToMarkdown(string rtf, Func<byte[], string> saveImage)
    {
        var document = new FlowDocument();
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            using var stream = new MemoryStream(RtfText.ToBytes(rtf));
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf);
        }

        var reader = new Reader(BodyFontSize(document), saveImage);
        var blocks = new List<MdBlock>();
        reader.ReadBlocks(document.Blocks, blocks);
        return MdDoc.Write(blocks);
    }

    /// <summary>The font size most of the text is written in: bigger paragraphs are headings.</summary>
    private static double BodyFontSize(FlowDocument document)
    {
        var sizes = new Dictionary<double, int>();
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Run run)
                {
                    var size = Math.Round(run.FontSize, 1);
                    sizes[size] = sizes.GetValueOrDefault(size) + run.Text.Length;
                }
                else if (inline is Span span) Walk(span.Inlines);
            }
        }
        foreach (var paragraph in MarkdownFlow.Paragraphs(document)) Walk(paragraph.Inlines);
        return sizes.Count == 0 ? 16 : sizes.OrderByDescending(p => p.Value).First().Key;
    }

    private sealed class Reader
    {
        private readonly double _body;
        private readonly Func<byte[], string> _saveImage;

        public Reader(double body, Func<byte[], string> saveImage)
        {
            _body = body <= 0 ? 16 : body;
            _saveImage = saveImage;
        }

        private static bool IsMono(FontFamily family) =>
            family.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
            family.Source.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
            family.Source.Contains("Lucida Console", StringComparison.OrdinalIgnoreCase);

        private static bool HasStrike(TextDecorationCollection? decorations) =>
            decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);

        /// <summary>A picture anywhere in the line, also inside a link or a styled span.</summary>
        private static bool HasPicture(InlineCollection inlines) =>
            inlines.Any(i => i is InlineUIContainer || (i is Span span && HasPicture(span.Inlines)));

        /// <summary>Every paragraph of a table cell, also those inside lists, sections and nested tables.</summary>
        private static IEnumerable<Paragraph> ParagraphsOf(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        yield return paragraph;
                        break;
                    case Section section:
                        foreach (var inner in ParagraphsOf(section.Blocks)) yield return inner;
                        break;
                    case List list:
                        foreach (var item in list.ListItems)
                        foreach (var inner in ParagraphsOf(item.Blocks)) yield return inner;
                        break;
                    case Table table:
                        foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                        foreach (var inner in ParagraphsOf(cell.Blocks)) yield return inner;
                        break;
                }
            }
        }

        public void ReadBlocks(BlockCollection source, List<MdBlock> output)
        {
            foreach (var block in source)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        ReadParagraph(paragraph, output);
                        break;
                    case Section section:
                        ReadBlocks(section.Blocks, output);
                        break;
                    case List list:
                        ReadList(list, output);
                        break;
                    case Table table:
                        ReadTable(table, output);
                        break;
                    case BlockUIContainer { Child: Image image }:
                        if (ImageInline(image) is { } picture) output.Add(new MdParagraph { Inlines = { picture } });
                        break;
                }
            }
        }

        private void ReadParagraph(Paragraph paragraph, List<MdBlock> output)
        {
            var runs = new List<Run>();
            void Collect(InlineCollection inlines)
            {
                foreach (var inline in inlines)
                {
                    if (inline is Run run) { if (run.Text.Length > 0) runs.Add(run); }
                    else if (inline is Span span) Collect(span.Inlines);
                }
            }
            Collect(paragraph.Inlines);
            var hasPicture = HasPicture(paragraph.Inlines);

            if (runs.Count == 0 && !hasPicture)
            {
                // An empty paragraph with a line below it was a horizontal rule.
                if (paragraph.BorderThickness.Bottom > 0 && paragraph.BorderThickness.Top == 0) output.Add(new MdRule());
                return;
            }

            // Code: every character in a fixed-width font. Consecutive code paragraphs make one block.
            if (runs.Count > 0 && !hasPicture && runs.All(r => IsMono(r.FontFamily)))
            {
                var code = MarkdownFlow.PlainText(paragraph.Inlines);
                if (output.Count > 0 && output[^1] is MdCodeBlock previous) previous.Code += "\n" + code;
                else output.Add(new MdCodeBlock { Code = code });
                return;
            }

            // Heading: the paragraph is clearly bigger than the body text.
            var level = 0;
            if (runs.Count > 0)
            {
                var size = runs.Sum(r => r.FontSize * r.Text.Length) / runs.Sum(r => r.Text.Length);
                var ratio = size / _body;
                level = ratio >= 1.7 ? 1 : ratio >= 1.4 ? 2 : ratio >= 1.2 ? 3 : 0;
            }

            var inlines = new List<MdInline>();
            ReadInlines(paragraph.Inlines, inlines, null, false, inHeading: level > 0);
            var normalized = MdDoc.Normalize(inlines);
            if (normalized.Count == 0) return;

            if (level > 0)
            {
                foreach (var run in normalized.Where(r => r.Kind == MdInlineKind.LineBreak).ToList())
                    normalized[normalized.IndexOf(run)] = MdInline.Plain(" ");
                output.Add(new MdHeading { Level = level, Inlines = normalized });
            }
            else
            {
                output.Add(new MdParagraph { Inlines = normalized });
            }
        }

        private void ReadList(List list, List<MdBlock> output)
        {
            var md = new MdList
            {
                Ordered = list.MarkerStyle is not (TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None),
                Start = Math.Max(1, list.StartIndex)
            };
            foreach (var item in list.ListItems)
            {
                var mdItem = new MdListItem();
                ReadBlocks(item.Blocks, mdItem.Blocks);
                // "☐ text" / "☑ text" (written by the conversion to rich text) is a task again.
                if (mdItem.Blocks.FirstOrDefault() is MdParagraph { Inlines.Count: > 0 } first && first.Inlines[0].Kind == MdInlineKind.Text)
                {
                    var text = first.Inlines[0].Text;
                    if (text.StartsWith("☐") || text.StartsWith("☑"))
                    {
                        mdItem.Checked = text[0] == '☑';
                        first.Inlines[0].Text = text[1..].TrimStart();
                        if (first.Inlines[0].Text.Length == 0) first.Inlines.RemoveAt(0);
                    }
                }
                md.Items.Add(mdItem);
            }
            while (md.Items.Count > 1 && md.Items[^1].Blocks.Count == 0) md.Items.RemoveAt(md.Items.Count - 1);
            if (md.Items.Count > 0 && !(md.Items.Count == 1 && md.Items[0].Blocks.Count == 0)) output.Add(md);
        }

        private void ReadTable(Table table, List<MdBlock> output)
        {
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            if (rows.Count == 0) return;
            var md = new MdTable();
            var columns = rows.Max(r => r.Cells.Count);
            if (columns == 0) return;
            for (var i = 0; i < columns; i++) md.Align.Add(MdAlign.None);

            for (var r = 0; r < rows.Count; r++)
            {
                var cells = new List<List<MdInline>>();
                foreach (var cell in rows[r].Cells)
                {
                    var inlines = new List<MdInline>();
                    // A markdown cell is one line: lists and nested tables keep their text, one line each.
                    foreach (var paragraph in ParagraphsOf(cell.Blocks))
                    {
                        if (inlines.Count > 0) inlines.Add(MdInline.Break());
                        // The first row becomes the header of the markdown table: its bold is implied.
                        ReadInlines(paragraph.Inlines, inlines, null, false, inHeading: r == 0);
                    }
                    cells.Add(MdDoc.Normalize(inlines));
                }
                while (cells.Count < columns) cells.Add(new List<MdInline>());
                if (r == 0) md.Header = cells;
                else md.Rows.Add(cells);
            }
            output.Add(md);
        }

        private void ReadInlines(InlineCollection source, List<MdInline> output, string? href, bool strike, bool inHeading)
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
                            Bold = !inHeading && run.FontWeight.ToOpenTypeWeight() >= 600,
                            Italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique,
                            Strike = strike || HasStrike(run.TextDecorations),
                            Code = IsMono(run.FontFamily),
                            Href = href
                        });
                        break;

                    case LineBreak:
                        output.Add(MdInline.Break());
                        break;

                    case Hyperlink link:
                        ReadInlines(link.Inlines, output, link.NavigateUri?.OriginalString ?? href, strike || HasStrike(link.TextDecorations), inHeading);
                        break;

                    case Span span:
                        ReadInlines(span.Inlines, output, href, strike || HasStrike(span.TextDecorations), inHeading);
                        break;

                    case InlineUIContainer { Child: Image image }:
                        if (ImageInline(image) is { } picture)
                        {
                            picture.Href = href;
                            output.Add(picture);
                        }
                        break;
                }
            }
        }

        /// <summary>Stores a picture of the rich text as a PNG file and returns the markdown picture that points to it.</summary>
        private MdInline? ImageInline(Image image)
        {
            if (image.Source is not BitmapSource bitmap) return null;
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                var link = _saveImage(stream.ToArray());
                return new MdInline { Kind = MdInlineKind.Image, Source = link, Text = "picture" };
            }
            catch
            {
                return null;   // a picture that cannot be read is left out, the text is kept
            }
        }
    }
}
