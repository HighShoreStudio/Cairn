using System.Text;
using System.Text.RegularExpressions;

namespace HighshoreCairn.Services;

// =====================================================================================
//  Markdown document model (pure C#, no UI): the Documentation area parses a .md file
//  into these classes, shows / edits them as formatted text, and writes them back.
//  Parse(text) -> blocks -> Write(blocks) gives markdown that parses to the same blocks.
// =====================================================================================

public abstract class MdBlock { }

public sealed class MdParagraph : MdBlock
{
    public List<MdInline> Inlines { get; set; } = new();
}

public sealed class MdHeading : MdBlock
{
    public int Level { get; set; } = 1;
    public List<MdInline> Inlines { get; set; } = new();
}

public sealed class MdCodeBlock : MdBlock
{
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class MdQuote : MdBlock
{
    public List<MdBlock> Blocks { get; set; } = new();
}

public sealed class MdList : MdBlock
{
    public bool Ordered { get; set; }
    public int Start { get; set; } = 1;
    public List<MdListItem> Items { get; set; } = new();
}

public sealed class MdListItem
{
    /// <summary>null = normal item, false = "[ ]", true = "[x]".</summary>
    public bool? Checked { get; set; }
    public List<MdBlock> Blocks { get; set; } = new();
}

public sealed class MdRule : MdBlock { }

public enum MdAlign { None, Left, Center, Right }

public sealed class MdTable : MdBlock
{
    public List<MdAlign> Align { get; set; } = new();
    public List<List<MdInline>> Header { get; set; } = new();
    public List<List<List<MdInline>>> Rows { get; set; } = new();
}

/// <summary>YAML front matter ("---" block at the very top of a file): kept as it is.</summary>
public sealed class MdFrontMatter : MdBlock
{
    public string Raw { get; set; } = "";
}

public enum MdInlineKind { Text, Image, LineBreak }

/// <summary>
/// A piece of text with one formatting. Inline content is a flat list of these runs:
/// a link is simply a sequence of runs that share the same <see cref="Href"/>.
/// </summary>
public sealed class MdInline
{
    public MdInlineKind Kind { get; set; } = MdInlineKind.Text;
    /// <summary>The text (for an image: its alternative text).</summary>
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Strike { get; set; }
    public bool Code { get; set; }
    /// <summary>Link target, or null when the run is not part of a link.</summary>
    public string? Href { get; set; }
    /// <summary>True for [[wiki links]]: <see cref="Href"/> is the name of another document.</summary>
    public bool IsWiki { get; set; }
    /// <summary>Image file or address (Kind = Image).</summary>
    public string? Source { get; set; }
    /// <summary>Optional title of a link or an image: [text](target "title").</summary>
    public string? Title { get; set; }

    public static MdInline Plain(string text) => new() { Text = text };
    public static MdInline Break() => new() { Kind = MdInlineKind.LineBreak };

    public MdInline Clone() => (MdInline)MemberwiseClone();

    public bool SameFormat(MdInline o) =>
        Kind == o.Kind && Bold == o.Bold && Italic == o.Italic && Strike == o.Strike && Code == o.Code &&
        Href == o.Href && IsWiki == o.IsWiki && Source == o.Source && Title == o.Title;
}

/// <summary>A heading of a document, for the outline panel.</summary>
public sealed class MdOutlineItem
{
    public int Level { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Position among the headings of the document (0 = first heading).</summary>
    public int Index { get; set; }
    /// <summary>Line of the heading in the markdown source (0-based), or -1 when unknown.</summary>
    public int Line { get; set; } = -1;
    /// <summary>The text indented by the level of the heading (for a flat list that looks like a tree).</summary>
    public string Indented => new string(' ', Math.Max(0, Level - 1) * 3) + Text;
}

/// <summary>Markdown parser, writer and exporter of the Documentation area.</summary>
public static partial class MdDoc
{
    // ============================================================================ parsing

    public static List<MdBlock> Parse(string? text)
    {
        var lines = SplitLines(text);
        var blocks = new List<MdBlock>();
        var start = 0;

        // YAML front matter: only at the very top of the file.
        if (lines.Count > 1 && lines[0].TrimEnd() == "---")
        {
            var end = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
            if (end > 0)
            {
                blocks.Add(new MdFrontMatter { Raw = string.Join("\n", lines.GetRange(1, end - 1)) });
                start = end + 1;
            }
        }

        blocks.AddRange(ParseBlocks(lines.GetRange(start, lines.Count - start), 0));
        return blocks;
    }

    /// <summary>
    /// Splits the text into lines. Tabs used as indentation count as 4 spaces (so lists and code written
    /// with tabs work), except inside a fenced code block at the top level, where the text is kept as it is.
    /// </summary>
    public static List<string> SplitLines(string? text)
    {
        var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>(raw.Length);
        string? fence = null;
        foreach (var line in raw)
        {
            if (fence != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]) && Indent(line) < 4) fence = null;
                lines.Add(line);
                continue;
            }
            // Only fences that start at the left margin: inside lists and quotes the indentation matters.
            var open = FenceRx().Match(line);
            if (open.Success && Indent(line) == 0) fence = open.Groups[1].Value;
            lines.Add(ExpandTabs(line));
        }
        return lines;
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var sb = new StringBuilder();
        var leading = true;
        foreach (var ch in line)
        {
            if (ch == '\t' && leading) sb.Append(' ', 4 - sb.Length % 4);
            else
            {
                if (ch != ' ') leading = false;
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})\s*([^`]*)$")] private static partial Regex FenceRx();
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ ]*\1){2,}[ ]*$")] private static partial Regex RuleRx();
    [GeneratedRegex(@"^( *)([-+*]|\d{1,9}[.)])(?:( +)(.*)|)$")] private static partial Regex ListRx();
    [GeneratedRegex(@"^ {0,3}>")] private static partial Regex QuoteRx();
    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")] private static partial Regex TableSepRx();
    [GeneratedRegex(@"^\[([ xX])\](?: +|$)")] private static partial Regex TaskRx();

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static bool IsTableStart(List<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && lines[i + 1].Contains('-') &&
        TableSepRx().IsMatch(lines[i + 1]) &&
        (lines[i + 1].Contains('|') || lines[i].Trim().StartsWith('|'));

    /// <summary>True when the line begins a block that interrupts a paragraph.</summary>
    private static bool StartsBlock(List<string> lines, int i)
    {
        var line = lines[i];
        if (FenceRx().IsMatch(line) || HeadingRx().IsMatch(line) || QuoteRx().IsMatch(line) || RuleRx().IsMatch(line)) return true;
        var list = ListRx().Match(line);
        if (list.Success && list.Groups[4].Value.Length > 0 && Indent(line) < 4) return true;
        return IsTableStart(lines, i);
    }

    private static List<MdBlock> ParseBlocks(List<string> lines, int depth)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line)) { i++; continue; }

            // ---- fenced code block
            var fence = FenceRx().Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                var indent = Indent(line);
                var code = new List<string>();
                i++;
                while (i < lines.Count)
                {
                    var t = lines[i].Trim();
                    if (t.Length >= marker.Length && t.All(c => c == marker[0]) && Indent(lines[i]) < 4) break;
                    code.Add(lines[i].Length >= indent && Indent(lines[i]) >= indent ? lines[i][indent..] : lines[i].TrimStart(' '));
                    i++;
                }
                i++; // closing fence (or end of text)
                blocks.Add(new MdCodeBlock { Language = fence.Groups[2].Value.Trim(), Code = string.Join("\n", code) });
                continue;
            }

            // ---- heading
            var heading = HeadingRx().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MdHeading { Level = heading.Groups[1].Length, Inlines = ParseInline(heading.Groups[2].Value.Trim()) });
                i++;
                continue;
            }

            // ---- horizontal rule
            if (RuleRx().IsMatch(line))
            {
                blocks.Add(new MdRule());
                i++;
                continue;
            }

            // ---- quote
            if (QuoteRx().IsMatch(line) && depth < 12)
            {
                var inner = new List<string>();
                while (i < lines.Count && QuoteRx().IsMatch(lines[i]))
                {
                    var t = lines[i].TrimStart()[1..];
                    inner.Add(t.StartsWith(' ') ? t[1..] : t);
                    i++;
                }
                blocks.Add(new MdQuote { Blocks = ParseBlocks(inner, depth + 1) });
                continue;
            }

            // ---- list
            var item = ListRx().Match(line);
            if (item.Success && depth < 12)
            {
                blocks.Add(ParseList(lines, ref i, depth));
                continue;
            }

            // ---- table
            if (IsTableStart(lines, i))
            {
                blocks.Add(ParseTable(lines, ref i));
                continue;
            }

            // ---- code block written with 4 spaces (or a tab) of indentation
            if (Indent(line) >= 4)
            {
                var code = new List<string>();
                while (i < lines.Count)
                {
                    if (IsBlank(lines[i]))
                    {
                        // An empty line belongs to the block only when more indented lines follow.
                        var next = i + 1;
                        while (next < lines.Count && IsBlank(lines[next])) next++;
                        if (next >= lines.Count || Indent(lines[next]) < 4) break;
                        code.Add("");
                    }
                    else if (Indent(lines[i]) >= 4) code.Add(lines[i][4..]);
                    else break;
                    i++;
                }
                blocks.Add(new MdCodeBlock { Code = string.Join("\n", code) });
                continue;
            }

            // ---- paragraph (with setext headings: a line of === or --- right below the text)
            var para = new List<string> { line };
            i++;
            MdBlock? setext = null;
            while (i < lines.Count && !IsBlank(lines[i]))
            {
                var t = lines[i].Trim();
                if (t.Length > 0 && t.All(c => c == '=') && Indent(lines[i]) < 4)
                {
                    setext = new MdHeading { Level = 1 };
                    i++;
                    break;
                }
                if (t.Length > 1 && t.All(c => c == '-') && Indent(lines[i]) < 4)
                {
                    setext = new MdHeading { Level = 2 };
                    i++;
                    break;
                }
                if (StartsBlock(lines, i)) break;
                para.Add(lines[i]);
                i++;
            }

            var inlines = ParseInline(string.Join("\n", para.Select(l => l.Trim())));
            if (setext is MdHeading h)
            {
                h.Inlines = inlines;
                blocks.Add(h);
            }
            else
            {
                blocks.Add(new MdParagraph { Inlines = inlines });
            }
        }
        return blocks;
    }

    private static MdList ParseList(List<string> lines, ref int i, int depth)
    {
        var first = ListRx().Match(lines[i]);
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var kind = first.Groups[2].Value[^1]; // '-', '*', '+', '.' or ')': a different marker starts a new list
        var list = new MdList { Ordered = ordered };
        if (ordered && int.TryParse(first.Groups[2].Value[..^1], out var number)) list.Start = number;
        var baseIndent = first.Groups[1].Length;

        while (i < lines.Count)
        {
            var m = ListRx().Match(lines[i]);
            if (!m.Success) break;
            var indent = m.Groups[1].Length;
            if (indent > baseIndent + 1 || indent < baseIndent - 1) break;
            if (char.IsDigit(m.Groups[2].Value[0]) != ordered || m.Groups[2].Value[^1] != kind) break;
            if (RuleRx().IsMatch(lines[i])) break;

            var spaces = m.Groups[3].Length;
            var content = m.Groups[4].Value;
            // Column where the item's text starts: continuation lines must be indented at least that much.
            var contentIndent = indent + m.Groups[2].Length + (content.Length == 0 || spaces > 4 ? 1 : spaces);
            if (spaces > 4) content = new string(' ', spaces - 1) + content;

            var itemLines = new List<string> { content };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    // A blank line keeps the item open only when indented content follows.
                    var next = i + 1;
                    while (next < lines.Count && IsBlank(lines[next])) next++;
                    if (next < lines.Count && Indent(lines[next]) >= Math.Min(contentIndent, indent + 2))
                    {
                        itemLines.Add("");
                        i++;
                        continue;
                    }
                    break;
                }

                var lineIndent = Indent(line);
                if (lineIndent >= Math.Min(contentIndent, indent + 2))
                {
                    itemLines.Add(line[Math.Min(lineIndent, contentIndent)..]);
                    i++;
                    continue;
                }

                // Less indented: a sibling item, another block, or a "lazy" continuation of the paragraph.
                if (ListRx().IsMatch(line) || StartsBlock(lines, i)) break;
                if (itemLines.Count > 0 && itemLines[^1].Length == 0) break;
                itemLines.Add(line.TrimStart());
                i++;
            }

            var listItem = new MdListItem();
            var task = TaskRx().Match(itemLines[0]);
            if (task.Success)
            {
                listItem.Checked = task.Groups[1].Value != " ";
                itemLines[0] = itemLines[0][task.Length..];
            }
            listItem.Blocks = ParseBlocks(itemLines, depth + 1);
            list.Items.Add(listItem);

            // Blank lines between items do not end the list.
            var peek = i;
            while (peek < lines.Count && IsBlank(lines[peek])) peek++;
            if (peek > i && peek < lines.Count)
            {
                var nm = ListRx().Match(lines[peek]);
                if (nm.Success && Math.Abs(nm.Groups[1].Length - baseIndent) <= 1 &&
                    char.IsDigit(nm.Groups[2].Value[0]) == ordered && nm.Groups[2].Value[^1] == kind && !RuleRx().IsMatch(lines[peek]))
                    i = peek;
            }
        }
        return list;
    }

    private static MdTable ParseTable(List<string> lines, ref int i)
    {
        var table = new MdTable();
        var header = SplitRow(lines[i]);
        var separators = SplitRow(lines[i + 1]);
        foreach (var raw in separators)
        {
            var s = raw.Trim();
            var left = s.StartsWith(':');
            var right = s.EndsWith(':');
            table.Align.Add(left && right ? MdAlign.Center : right ? MdAlign.Right : left ? MdAlign.Left : MdAlign.None);
        }
        var columns = Math.Max(header.Count, table.Align.Count);
        while (table.Align.Count < columns) table.Align.Add(MdAlign.None);
        table.Header = Cells(header, columns);
        i += 2;
        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|'))
        {
            table.Rows.Add(Cells(SplitRow(lines[i]), columns));
            i++;
        }
        return table;
    }

    private static List<List<MdInline>> Cells(List<string> raw, int columns)
    {
        var cells = raw.Take(columns).Select(c => ParseInline(c.Trim())).ToList();
        foreach (var run in cells.SelectMany(c => c).Where(r => r.Code)) run.Text = run.Text.Replace("\\|", "|");
        while (cells.Count < columns) cells.Add(new List<MdInline>());
        return cells;
    }

    /// <summary>Splits "| a | b |" into its cells ("\|" is a literal pipe, pipes inside `code` are kept).</summary>
    private static List<string> SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|')) s = s[1..];
        var cells = new List<string>();
        var sb = new StringBuilder();
        var codeTicks = 0; // > 0 while inside a code span opened by that many backticks
        var endsWithPipe = false;
        for (var k = 0; k < s.Length; k++)
        {
            var ch = s[k];
            endsWithPipe = false;
            if (ch == '\\' && k + 1 < s.Length && (s[k + 1] == '|' || codeTicks == 0))
            {
                sb.Append(ch).Append(s[k + 1]);
                k++;
            }
            else if (ch == '`')
            {
                var run = RunLength(s, k, '`');
                if (codeTicks == 0)
                {
                    // Only a run that is closed later starts a code span.
                    if (FindBackticks(s, k + run, run) >= 0) codeTicks = run;
                }
                else if (run == codeTicks) codeTicks = 0;
                sb.Append('`', run);
                k += run - 1;
            }
            else if (ch == '|' && codeTicks == 0)
            {
                cells.Add(sb.ToString());
                sb.Clear();
                endsWithPipe = true;
            }
            else sb.Append(ch);
        }
        if (!endsWithPipe) cells.Add(sb.ToString());
        return cells;
    }

    // ============================================================================ inline parsing

    /// <summary>A piece of an inline text while it is being parsed: text, a finished run, or emphasis markers.</summary>
    private sealed class Node
    {
        public MdInline? Run;       // finished run (code, link text, image, line break ...)
        public string Text = "";    // plain text
        public char Delim;          // '*', '_' or '~' for a run of emphasis markers
        public int Count;           // markers not used yet
        public int Original;        // length of the run of markers
        public bool CanOpen, CanClose, Active = true;
        public bool Bold, Italic, Strike;
    }

    public static List<MdInline> ParseInline(string? text)
    {
        var result = new List<MdInline>();
        ParseInline(text ?? "", null, result, 0);
        return Normalize(result);
    }

    private static bool IsAlnum(char c) => char.IsLetterOrDigit(c);

    private static bool IsEscapable(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static void ParseInline(string s, string? href, List<MdInline> output, int depth)
    {
        var nodes = new List<Node>();
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            nodes.Add(new Node { Text = buf.ToString() });
            buf.Clear();
        }

        void Atom(MdInline run)
        {
            Flush();
            nodes.Add(new Node { Run = run });
        }

        var i = 0;
        var n = s.Length;
        while (i < n)
        {
            var c = s[i];

            // ---- escapes
            if (c == '\\')
            {
                if (i + 1 < n && s[i + 1] == '\n')
                {
                    Atom(MdInline.Break());
                    i += 2;
                    continue;
                }
                if (i + 1 < n && IsEscapable(s[i + 1]))
                {
                    buf.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                buf.Append(c);
                i++;
                continue;
            }

            // ---- line break inside a paragraph
            if (c == '\n')
            {
                while (buf.Length > 0 && buf[^1] == ' ') buf.Length--;
                Atom(MdInline.Break());
                i++;
                while (i < n && s[i] == ' ') i++;
                continue;
            }

            // ---- code span
            if (c == '`')
            {
                var ticks = RunLength(s, i, '`');
                var close = FindBackticks(s, i + ticks, ticks);
                if (close >= 0)
                {
                    var code = s.Substring(i + ticks, close - i - ticks).Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                    Atom(new MdInline { Text = code, Code = true });
                    i = close + ticks;
                    continue;
                }
                buf.Append(s, i, ticks);
                i += ticks;
                continue;
            }

            // ---- image ![alt](src) and embed ![[file]]
            if (c == '!' && i + 1 < n && s[i + 1] == '[')
            {
                if (i + 2 < n && s[i + 2] == '[')
                {
                    var end = s.IndexOf("]]", i + 3, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        var inner = s.Substring(i + 3, end - i - 3).Replace("\\|", "|");
                        var target = inner.Split('|')[0].Trim();
                        if (DocsService.IsImageName(target)) Atom(new MdInline { Kind = MdInlineKind.Image, Text = "", Source = target });
                        else Atom(new MdInline { Text = target, Href = target, IsWiki = true });
                        i = end + 2;
                        continue;
                    }
                }
                else if (TryParseLink(s, i + 1, out var alt, out var src, out var imageTitle, out var after))
                {
                    Atom(new MdInline { Kind = MdInlineKind.Image, Text = Unescape(alt), Source = src, Title = imageTitle });
                    i = after;
                    continue;
                }
            }

            // ---- links
            if (c == '[' && href is null)
            {
                if (i + 1 < n && s[i + 1] == '[')
                {
                    var end = s.IndexOf("]]", i + 2, StringComparison.Ordinal);
                    if (end > 0 && !s.AsSpan(i + 2, end - i - 2).Contains('\n'))
                    {
                        var inner = s.Substring(i + 2, end - i - 2).Replace("\\|", "|");
                        var bar = inner.IndexOf('|');
                        var target = (bar >= 0 ? inner[..bar] : inner).Trim();
                        var label = bar >= 0 ? inner[(bar + 1)..].Trim() : target;
                        if (target.Length > 0)
                        {
                            Atom(new MdInline { Text = label.Length > 0 ? label : target, Href = target, IsWiki = true });
                            i = end + 2;
                            continue;
                        }
                    }
                }
                else if (depth < 8 && TryParseLink(s, i, out var label, out var url, out var linkTitle, out var after))
                {
                    var inner = new List<MdInline>();
                    ParseInline(label, url, inner, depth + 1);
                    if (inner.Count == 0) inner.Add(new MdInline { Text = url, Href = url });
                    foreach (var run in inner)
                    {
                        if (run.Kind == MdInlineKind.Text) run.Title = linkTitle;
                        Atom(run);
                    }
                    i = after;
                    continue;
                }
            }

            // ---- <br>, <http://autolink>
            if (c == '<')
            {
                var br = BrRx().Match(s, i);
                if (br.Success && br.Index == i)
                {
                    Atom(MdInline.Break());
                    i += br.Length;
                    continue;
                }
                var close = s.IndexOf('>', i + 1);
                if (close > 0 && href is null)
                {
                    var inner = s.Substring(i + 1, close - i - 1);
                    if (IsUrl(inner) && !inner.Contains(' '))
                    {
                        Atom(new MdInline { Text = inner, Href = inner });
                        i = close + 1;
                        continue;
                    }
                }
            }

            // ---- bare links: https://example.com
            if ((c == 'h' || c == 'H') && href is null && BareUrlLength(s, i) is > 0 and var urlLength)
            {
                var url = s.Substring(i, urlLength);
                Atom(new MdInline { Text = url, Href = url });
                i += urlLength;
                continue;
            }

            // ---- emphasis markers: **bold**, *italic*, _italic_, ~~strike~~ (matched below)
            if (c == '*' || c == '_' || c == '~')
            {
                var run = RunLength(s, i, c);
                var prev = i > 0 ? s[i - 1] : ' ';
                var next = i + run < n ? s[i + run] : ' ';
                var canOpen = !char.IsWhiteSpace(next) && (c != '_' || !IsAlnum(prev));
                var canClose = !char.IsWhiteSpace(prev) && (c != '_' || !IsAlnum(next));
                if ((c == '~' && run < 2) || (!canOpen && !canClose))
                {
                    buf.Append(c, run);
                }
                else
                {
                    Flush();
                    nodes.Add(new Node { Delim = c, Count = run, Original = run, CanOpen = canOpen, CanClose = canClose });
                }
                i += run;
                continue;
            }

            buf.Append(c);
            i++;
        }
        Flush();

        MatchEmphasis(nodes);

        foreach (var node in nodes)
        {
            MdInline run;
            if (node.Run != null) run = node.Run;
            else if (node.Delim != '\0')
            {
                if (node.Count == 0) continue;
                run = new MdInline { Text = new string(node.Delim, node.Count) };   // unmatched markers are plain text
            }
            else run = new MdInline { Text = node.Text };

            run.Bold |= node.Bold;
            run.Italic |= node.Italic;
            run.Strike |= node.Strike;
            if (href != null && run.Href is null && run.Kind != MdInlineKind.LineBreak) run.Href = href;
            output.Add(run);
        }
    }

    /// <summary>
    /// Pairs the emphasis markers the way CommonMark does: every closing run looks back for the nearest
    /// opening run of the same character; two markers on each side make bold, one makes italic.
    /// </summary>
    private static void MatchEmphasis(List<Node> nodes)
    {
        for (var c = 0; c < nodes.Count; c++)
        {
            var closer = nodes[c];
            if (closer.Delim == '\0' || !closer.Active || !closer.CanClose) continue;

            while (closer.Count > 0)
            {
                var o = c - 1;
                for (; o >= 0; o--)
                {
                    var opener = nodes[o];
                    if (opener.Delim != closer.Delim || !opener.Active || !opener.CanOpen || opener.Count == 0) continue;
                    if (closer.Delim == '~' && (opener.Count < 2 || closer.Count < 2)) continue;
                    // "rule of 3": a run that can both open and close does not pair when the lengths add up to
                    // a multiple of 3 (it keeps "*a**b**c*" from pairing the wrong markers).
                    if ((opener.CanClose || closer.CanOpen) && (opener.Original + closer.Original) % 3 == 0 &&
                        !(opener.Original % 3 == 0 && closer.Original % 3 == 0)) continue;
                    break;
                }
                if (o < 0) break;

                var open = nodes[o];
                var use = closer.Delim == '~' || (open.Count >= 2 && closer.Count >= 2) ? 2 : 1;
                for (var k = o + 1; k < c; k++)
                {
                    var inner = nodes[k];
                    if (closer.Delim == '~') inner.Strike = true;
                    else if (use == 2) inner.Bold = true;
                    else inner.Italic = true;
                    if (inner.Delim != '\0') inner.Active = false; // markers in between can no longer pair
                }
                open.Count -= use;
                closer.Count -= use;
            }

            if (closer.Count > 0 && !closer.CanOpen) closer.Active = false;
        }
    }

    [GeneratedRegex(@"\G<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BrRx();
    [GeneratedRegex(@"\s+(""[^""]*""|'[^']*')$")] private static partial Regex LinkTitleRx();

    /// <summary>
    /// Length of the web address that starts at position i ("https://..." up to the next space, without
    /// trailing punctuation), or 0. The parser turns such text into a link and the writer leaves it as it is.
    /// </summary>
    private static int BareUrlLength(string s, int i)
    {
        if (i > 0 && IsAlnum(s[i - 1])) return 0;
        if (!StartsWithAt(s, i, "http://") && !StartsWithAt(s, i, "https://")) return 0;
        var end = i;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '`') end++;
        while (end > i && ".,;:!?)]}'\"*_~".Contains(s[end - 1])) end--;
        return end - i > 8 ? end - i : 0;
    }

    private static bool StartsWithAt(string s, int i, string value) =>
        string.Compare(s, i, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    public static bool IsUrl(string? s) =>
        s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                      s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                      s.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    private static int RunLength(string s, int i, char c)
    {
        var k = i;
        while (k < s.Length && s[k] == c) k++;
        return k - i;
    }

    private static int FindBackticks(string s, int from, int count)
    {
        var k = from;
        while (k < s.Length)
        {
            if (s[k] != '`') { k++; continue; }
            var run = RunLength(s, k, '`');
            if (run == count) return k;
            k += run;
        }
        return -1;
    }

    /// <summary>Parses "[label](target "title")" starting at the '['.</summary>
    private static bool TryParseLink(string s, int i, out string label, out string url, out string? title, out int after)
    {
        label = url = "";
        title = null;
        after = i;
        if (i >= s.Length || s[i] != '[') return false;

        var depth = 0;
        var k = i;
        var close = -1;
        while (k < s.Length)
        {
            var ch = s[k];
            if (ch == '\\') { k += 2; continue; }
            if (ch == '`')
            {
                var ticks = RunLength(s, k, '`');
                var codeEnd = FindBackticks(s, k + ticks, ticks);
                k = codeEnd >= 0 ? codeEnd + ticks : k + ticks;
                continue;
            }
            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0) { close = k; break; }
            }
            k++;
        }
        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(') return false;

        // The target runs up to the matching ')': spaces are accepted ("my file.md"), "<...>" is optional
        // and a "title" in quotes may follow.
        var p = close + 2;
        var parens = 0;
        var end = -1;
        for (var q = p; q < s.Length; q++)
        {
            var ch = s[q];
            if (ch == '\\' && q + 1 < s.Length) { q++; continue; }
            if (ch == '\n') break;
            if (ch == '(') parens++;
            else if (ch == ')')
            {
                if (parens == 0) { end = q; break; }
                parens--;
            }
        }
        if (end < 0) return false;

        var raw = s.Substring(p, end - p).Trim();
        var titleMatch = LinkTitleRx().Match(raw);
        if (titleMatch.Success)
        {
            title = titleMatch.Groups[1].Value[1..^1];
            if (title.Length == 0) title = null;
            raw = raw[..titleMatch.Index].TrimEnd();
        }
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>') raw = raw[1..^1];
        else raw = Unescape(raw);
        if (raw.Length == 0) return false;
        p = end;

        label = s.Substring(i + 1, close - i - 1);
        url = raw;
        if (!IsUrl(url))
        {
            try { url = Uri.UnescapeDataString(url); }
            catch { /* keep as written */ }
        }
        after = p + 1;
        return true;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder();
        for (var k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length && IsEscapable(s[k + 1])) k++;
            sb.Append(s[k]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Canonical form of inline content: neighbouring runs with the same format are merged, empty runs
    /// are dropped and the spaces at the edges of a formatted run become plain text
    /// (markdown cannot express "bold space"). Parse and Write both produce / expect this form.
    /// </summary>
    public static List<MdInline> Normalize(IEnumerable<MdInline> inlines)
    {
        var merged = Merge(inlines);

        // Spaces at the edges of a formatted run are written outside its markers.
        var split = new List<MdInline>();
        foreach (var run in merged)
        {
            if (run.Kind != MdInlineKind.Text || run.Code || !(run.Bold || run.Italic || run.Strike))
            {
                split.Add(run);
                continue;
            }
            var trimmedStart = run.Text.TrimStart(' ');
            var lead = run.Text[..(run.Text.Length - trimmedStart.Length)];
            var core = trimmedStart.TrimEnd(' ');
            var trail = trimmedStart[core.Length..];
            if (lead.Length > 0) split.Add(new MdInline { Text = lead, Href = run.Href, IsWiki = run.IsWiki, Title = run.Title });
            if (core.Length > 0)
            {
                var copy = run.Clone();
                copy.Text = core;
                split.Add(copy);
            }
            if (trail.Length > 0) split.Add(new MdInline { Text = trail, Href = run.Href, IsWiki = run.IsWiki, Title = run.Title });
        }
        var result = Merge(split);

        // Line breaks at the edges, and the spaces around a line break, never survive a round trip.
        bool changed;
        do
        {
            changed = false;
            while (result.Count > 0 && result[0].Kind == MdInlineKind.LineBreak) { result.RemoveAt(0); changed = true; }
            while (result.Count > 0 && result[^1].Kind == MdInlineKind.LineBreak) { result.RemoveAt(result.Count - 1); changed = true; }
            for (var k = 0; k < result.Count; k++)
            {
                var run = result[k];
                if (run.Kind != MdInlineKind.Text || run.Code) continue;
                var before = run.Text;
                if (k == 0 || result[k - 1].Kind == MdInlineKind.LineBreak) run.Text = run.Text.TrimStart(' ');
                if (k == result.Count - 1 || result[k + 1].Kind == MdInlineKind.LineBreak) run.Text = run.Text.TrimEnd(' ');
                if (run.Text != before) changed = true;
            }
            if (result.RemoveAll(r => r.Kind == MdInlineKind.Text && r.Text.Length == 0) > 0) changed = true;
            // Two line breaks in a row would start a new paragraph.
            for (var k = result.Count - 1; k > 0; k--)
            {
                if (result[k].Kind == MdInlineKind.LineBreak && result[k - 1].Kind == MdInlineKind.LineBreak)
                {
                    result.RemoveAt(k);
                    changed = true;
                }
            }
        } while (changed);
        return Merge(result);
    }

    private static List<MdInline> Merge(IEnumerable<MdInline> inlines)
    {
        var result = new List<MdInline>();
        foreach (var run in inlines)
        {
            if (run.Kind == MdInlineKind.Text)
            {
                if (run.Text.Length == 0) continue;
                // A code span made only of spaces cannot be written: it becomes plain text.
                var current = run.Code && run.Text.Trim(' ').Length == 0
                    ? new MdInline { Text = run.Text, Href = run.Href, IsWiki = run.IsWiki, Title = run.Title }
                    : run;
                if (current.Text.Contains('\n') || current.Text.Contains('\r'))
                {
                    // A new line inside a run is a line break.
                    var parts = current.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (var p = 0; p < parts.Length; p++)
                    {
                        if (p > 0) result.Add(MdInline.Break());
                        if (parts[p].Length == 0) continue;
                        var piece = current.Clone();
                        piece.Text = parts[p];
                        Append(result, piece);
                    }
                    continue;
                }
                Append(result, current.Clone());
            }
            else
            {
                result.Add(run.Clone());
            }
        }
        return result;

        static void Append(List<MdInline> list, MdInline run)
        {
            if (list.Count > 0 && list[^1].Kind == MdInlineKind.Text && list[^1].SameFormat(run)) list[^1].Text += run.Text;
            else list.Add(run);
        }
    }

    // ============================================================================ writing

    public static string Write(IEnumerable<MdBlock> blocks)
    {
        var lines = new List<string>();
        var list = blocks.ToList();
        WriteBlocks(list, lines);
        // "---" on the first line would be read back as the start of a front matter block.
        if (list.Count > 0 && list[0] is MdRule && lines.Count > 0) lines[0] = "***";
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    private static void WriteBlocks(List<MdBlock> blocks, List<string> lines)
    {
        var alternate = false;
        for (var b = 0; b < blocks.Count; b++)
        {
            if (b > 0) lines.Add("");
            // Two lists in a row would be read back as one: the second one uses the other marker ("*" or "1)").
            alternate = b > 0 && blocks[b] is MdList current && blocks[b - 1] is MdList previous &&
                        current.Ordered == previous.Ordered && !alternate;
            if (blocks[b] is MdList list) WriteList(list, lines, alternate);
            else WriteBlock(blocks[b], lines);
        }
    }

    private static void WriteBlock(MdBlock block, List<string> lines)
    {
        switch (block)
        {
            case MdFrontMatter front:
                lines.Add("---");
                lines.AddRange(front.Raw.Split('\n'));
                lines.Add("---");
                break;

            case MdHeading heading:
                var title = WriteInline(heading.Inlines, inTable: false).Replace("\n", " ").Trim();
                if (title.EndsWith('#')) title = title[..^1] + "\\#";
                lines.Add((new string('#', Math.Clamp(heading.Level, 1, 6)) + " " + title).TrimEnd());
                break;

            case MdParagraph paragraph:
                var text = WriteInline(paragraph.Inlines, inTable: false);
                foreach (var line in text.Split('\n')) lines.Add(EscapeLineStart(line));
                break;

            case MdCodeBlock code:
                var fence = "```";
                while (code.Code.Contains(fence)) fence += "`";
                lines.Add(fence + code.Language.Trim());
                lines.AddRange(code.Code.Replace("\r", "").Split('\n'));
                lines.Add(fence);
                break;

            case MdRule:
                lines.Add("---");
                break;

            case MdQuote quote:
                var inner = new List<string>();
                WriteBlocks(quote.Blocks, inner);
                if (inner.Count == 0) inner.Add("");
                foreach (var line in inner) lines.Add(line.Length == 0 ? ">" : "> " + line);
                break;

            case MdList list:
                WriteList(list, lines, false);
                break;

            case MdTable table:
                WriteTable(table, lines);
                break;
        }
    }

    private static void WriteList(MdList list, List<string> lines, bool alternate)
    {
        // "Tight" list: every item is one paragraph, optionally followed by nested lists.
        var tight = list.Items.All(item =>
            item.Blocks.Count == 0 ||
            (item.Blocks[0] is MdParagraph && item.Blocks.Skip(1).All(x => x is MdList)));

        var number = list.Start;
        for (var k = 0; k < list.Items.Count; k++)
        {
            var item = list.Items[k];
            if (k > 0 && !tight) lines.Add("");

            var marker = list.Ordered ? $"{number++}{(alternate ? ")" : ".")} " : alternate ? "* " : "- ";
            var pad = new string(' ', marker.Length);
            var task = item.Checked is null ? "" : item.Checked.Value ? "[x] " : "[ ] ";

            var inner = new List<string>();
            var innerAlternate = false;
            for (var b = 0; b < item.Blocks.Count; b++)
            {
                if (b > 0 && !(tight && item.Blocks[b] is MdList)) inner.Add("");
                innerAlternate = b > 0 && item.Blocks[b] is MdList current && item.Blocks[b - 1] is MdList previous &&
                                 current.Ordered == previous.Ordered && !innerAlternate;
                if (item.Blocks[b] is MdList nested) WriteList(nested, inner, innerAlternate);
                else WriteBlock(item.Blocks[b], inner);
            }
            if (inner.Count == 0) inner.Add("");

            // Only a paragraph can share the line with the marker and the task box.
            var firstIsText = item.Blocks.Count > 0 && item.Blocks[0] is MdParagraph;
            if (!firstIsText && (task.Length > 0 || item.Blocks.Count > 0))
            {
                lines.Add((marker + task).TrimEnd());
                foreach (var line in inner) lines.Add(line.Length == 0 ? "" : pad + line);
                continue;
            }

            lines.Add((marker + task + inner[0]).TrimEnd());
            for (var j = 1; j < inner.Count; j++) lines.Add(inner[j].Length == 0 ? "" : pad + inner[j]);
        }
    }

    private static void WriteTable(MdTable table, List<string> lines)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        if (columns == 0) return;

        string Cell(List<List<MdInline>> row, int c)
        {
            var cell = c < row.Count ? WriteInline(row[c], inTable: true).Replace("\n", "<br>") : "";
            return cell.Length == 0 ? " " : cell;
        }

        lines.Add("| " + string.Join(" | ", Enumerable.Range(0, columns).Select(c => Cell(table.Header, c))) + " |");
        lines.Add("| " + string.Join(" | ", Enumerable.Range(0, columns).Select(c =>
            (c < table.Align.Count ? table.Align[c] : MdAlign.None) switch
            {
                MdAlign.Left => ":---",
                MdAlign.Center => ":---:",
                MdAlign.Right => "---:",
                _ => "---"
            })) + " |");
        foreach (var row in table.Rows)
            lines.Add("| " + string.Join(" | ", Enumerable.Range(0, columns).Select(c => Cell(row, c))) + " |");
    }

    [GeneratedRegex(@"^(#{1,6}(\s|$)|>|[-+*](\s|$)|\d{1,9}[.)](\s|$)|```|~~~|=+\s*$|-{2,}\s*$)")]
    private static partial Regex BlockStartRx();

    /// <summary>A paragraph line that looks like the start of another block gets a backslash in front.</summary>
    private static string EscapeLineStart(string line)
    {
        if (line.Length == 0) return line;
        if (RuleRx().IsMatch(line) || TableSepRx().IsMatch(line) && line.Contains('-')) return "\\" + line;
        var m = BlockStartRx().Match(line);
        if (!m.Success) return line;
        if (char.IsDigit(line[0]))
        {
            var dot = line.IndexOfAny(new[] { '.', ')' });
            return line[..dot] + "\\" + line[dot..];
        }
        return "\\" + line;
    }

    // ---------------------------------------------------------------------------- inline writing

    public static string WriteInline(IEnumerable<MdInline> inlines, bool inTable = false)
    {
        // A [[wiki link]] cannot contain formatting: its runs become one token with the format of the first.
        var runs = new List<MdInline>();
        foreach (var run in Normalize(inlines))
        {
            if (run.Kind == MdInlineKind.Text && run.IsWiki && runs.Count > 0 &&
                runs[^1].Kind == MdInlineKind.Text && runs[^1].IsWiki && runs[^1].Href == run.Href)
                runs[^1].Text += run.Text;
            else
                runs.Add(run);
        }

        static string? Link(MdInline run) => run.IsWiki ? null : run.Href;

        var sb = new StringBuilder();
        var k = 0;
        while (k < runs.Count)
        {
            var run = runs[k];
            if (run.Kind == MdInlineKind.LineBreak)
            {
                sb.Append('\n');
                k++;
                continue;
            }

            // A group = the runs sharing the same link (or the runs up to the next link / line break).
            var end = k + 1;
            while (end < runs.Count && runs[end].Kind != MdInlineKind.LineBreak && Link(runs[end]) == Link(run)) end++;
            var group = runs.GetRange(k, end - k);
            var next = end < runs.Count && runs[end].Kind == MdInlineKind.Text && runs[end].Text.Length > 0 && Link(runs[end]) is null && !runs[end].IsWiki
                ? runs[end].Text[0] : ' ';
            var prev = sb.Length > 0 ? sb[^1] : ' ';

            if (Link(run) is null)
            {
                sb.Append(WriteStyled(group, prev, next, inTable));
            }
            else
            {
                // "!" right before "[" would turn the link into a picture.
                if (sb.Length > 0 && sb[^1] == '!' && (sb.Length < 2 || sb[^2] != '\\')) sb.Insert(sb.Length - 1, '\\');
                // A link shown as its own address is written as an autolink: <https://example.com>
                if (group.Count == 1 && run.Kind == MdInlineKind.Text && run.Text == run.Href && IsUrl(run.Href) && run.Title is null &&
                    !run.Bold && !run.Italic && !run.Strike && !run.Code && !run.Href.Any(c => char.IsWhiteSpace(c) || c == '<' || c == '>') &&
                    !(inTable && run.Href.Contains('|')))
                {
                    // ...or simply as the address, when it stands alone between spaces (it is read back as the same link).
                    var standsAlone = !inTable && (sb.Length == 0 || char.IsWhiteSpace(sb[^1]) || sb[^1] == '(') &&
                        BareUrlLength(run.Href, 0) == run.Href.Length && EndsBareUrl(runs, end);
                    if (standsAlone) sb.Append(run.Href);
                    else sb.Append('<').Append(run.Href).Append('>');
                }
                else
                {
                    var label = WriteStyled(group, '[', ']', inTable, inLink: true);
                    sb.Append('[').Append(label).Append("](").Append(EscapeUrl(run.Href!)).Append(TitleSuffix(run.Title)).Append(')');
                }
            }
            k = end;
        }
        return sb.ToString();
    }

    private static string TitleSuffix(string? title) =>
        string.IsNullOrEmpty(title) ? "" : " \"" + title.Replace("\"", "'").Replace("\n", " ") + "\"";

    private static string WikiToken(MdInline run, bool inTable)
    {
        var target = (run.Href ?? "").Trim();
        var label = run.Text.Trim();
        var link = label.Length == 0 || label == target
            ? $"[[{target}]]"
            : $"[[{target}|{label.Replace("|", "/").Replace("]]", "] ]").Replace("[[", "[ [")}]]";
        return inTable ? link.Replace("|", "\\|") : link;
    }

    /// <summary>
    /// True when what follows a web address ends it for the parser too: the end of the text, a space, or
    /// punctuation followed by a space ("https://example.com, and..."): BareUrlLength leaves that punctuation out.
    /// </summary>
    private static bool EndsBareUrl(List<MdInline> runs, int index)
    {
        if (index >= runs.Count || runs[index].Kind == MdInlineKind.LineBreak) return true;
        var after = runs[index];
        if (after.Kind != MdInlineKind.Text || after.Href != null || after.IsWiki || after.Bold || after.Italic || after.Strike || after.Code)
            return false;
        var k = 0;
        while (k < after.Text.Length && ".,;:!?)]}'\"".Contains(after.Text[k])) k++;
        if (k < after.Text.Length) return char.IsWhiteSpace(after.Text[k]);
        return index + 1 >= runs.Count || runs[index + 1].Kind == MdInlineKind.LineBreak;
    }

    /// <summary>A link target as it must be written between the parentheses of "[text](target)".</summary>
    public static string LinkTarget(string url) => EscapeUrl(url);

    private static string EscapeUrl(string url)
    {
        var needsBrackets = url.Any(c => c == ' ' || c == '(' || c == ')');
        return needsBrackets ? "<" + url.Replace(">", "%3E") + ">" : url;
    }

    private static readonly char[] MarkOrder = { 'B', 'I', 'S' };

    private static bool Has(MdInline run, char mark) => mark switch
    {
        'B' => run.Bold,
        'I' => run.Italic,
        _ => run.Strike
    };

    /// <summary>
    /// Writes runs with nested markers. Spaces at the edges of a formatted run are moved outside
    /// the markers ("**bold** text", never "**bold **text") because markdown would not close them.
    /// </summary>
    private static string WriteStyled(List<MdInline> runs, char prevChar, char nextChar, bool inTable, bool inLink = false)
    {
        var sb = new StringBuilder();
        var stack = new List<(char Mark, string Token)>();
        var pending = "";

        char Last() => sb.Length > 0 ? sb[^1] : prevChar;

        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Kind == MdInlineKind.Image)
            {
                while (stack.Count > 0) Pop();
                sb.Append(pending);
                pending = "";
                sb.Append("![").Append(EscapeText(run.Text, inTable, inLink: true)).Append("](").Append(EscapeUrl(run.Source ?? ""))
                  .Append(TitleSuffix(run.Title)).Append(')');
                continue;
            }

            var text = run.Text;
            string lead, core, trail;
            if (run.IsWiki)
            {
                var trimmedLead = text.TrimStart(' ');
                (lead, core, trail) = (text[..(text.Length - trimmedLead.Length)], "[[", text[text.TrimEnd(' ').Length..]);
            }
            else if (run.Code)
            {
                (lead, core, trail) = ("", text, "");
            }
            else
            {
                var trimmedStart = text.TrimStart(' ');
                lead = text[..(text.Length - trimmedStart.Length)];
                core = trimmedStart.TrimEnd(' ');
                trail = trimmedStart[core.Length..];
            }
            if (core.Length == 0)
            {
                // Spaces between two formatted runs: the markers they do not share close before them.
                while (stack.Any(m => !Has(run, m.Mark))) Pop();
                pending += text;
                continue;
            }

            // Close what the new run does not have (markers close in the reverse order they opened).
            while (stack.Any(m => !Has(run, m.Mark))) Pop();
            sb.Append(pending).Append(lead);
            pending = "";

            foreach (var mark in MarkOrder)
            {
                if (!Has(run, mark) || stack.Any(m => m.Mark == mark)) continue;
                var token = mark switch
                {
                    'B' => "**",
                    'S' => "~~",
                    _ => ItalicToken(runs, r, Last(), nextChar, stack.Select(m => m.Mark).ToList())
                };
                stack.Add((mark, token));
                sb.Append(token);
            }

            if (run.IsWiki)
            {
                if (sb.Length > 0 && sb[^1] == '!' && (sb.Length < 2 || sb[^2] != '\\')) sb.Insert(sb.Length - 1, '\\');
                sb.Append(WikiToken(run, inTable));
            }
            else sb.Append(run.Code ? CodeSpan(core, inTable) : EscapeText(core, inTable, inLink));
            pending = trail;
        }

        while (stack.Count > 0) Pop();
        sb.Append(pending);
        return sb.ToString();

        void Pop()
        {
            sb.Append(stack[^1].Token);
            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>
    /// "_" is the clearest italic marker next to "**", but markdown ignores it inside a word:
    /// it is used only when both of its ends touch something that is not a letter or a digit.
    /// </summary>
    private static string ItalicToken(List<MdInline> runs, int index, char before, char groupNext, List<char> below)
    {
        if (IsAlnum(before)) return "*";

        // Find what follows the closing marker.
        for (var r = index + 1; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Kind != MdInlineKind.Text) return "_";
            if (!run.Code && run.Text.Trim(' ').Length == 0) return "_";      // a space follows
            var closesHere = !run.Italic || below.Any(m => !Has(run, m));
            if (!closesHere) continue;
            if (below.Any(m => !Has(run, m)) || run.Code || run.IsWiki) return "_"; // another marker follows
            if (MarkOrder.Any(m => m != 'I' && Has(run, m) && !below.Contains(m))) return "_";
            return IsAlnum(run.Text[0]) ? "*" : "_";
        }
        return IsAlnum(groupNext) ? "*" : "_";
    }

    private static string CodeSpan(string code, bool inTable)
    {
        var longest = 0;
        var current = 0;
        foreach (var ch in code)
        {
            current = ch == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        var ticks = new string('`', longest + 1);
        var pad = code.StartsWith('`') || code.EndsWith('`') || (code.StartsWith(' ') && code.EndsWith(' ')) ? " " : "";
        var text = ticks + pad + code + pad + ticks;
        return inTable ? text.Replace("|", "\\|") : text;
    }

    private static string EscapeText(string text, bool inTable, bool inLink = false)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var k = 0; k < text.Length; k++)
        {
            var c = text[k];
            // A web address in plain text is read back as a link, character by character: no escaping in it.
            if ((c == 'h' || c == 'H') && !inLink && BareUrlLength(text, k) is > 0 and var urlLength)
            {
                var url = text.Substring(k, urlLength);
                sb.Append(inTable ? url.Replace("|", "%7C") : url);
                k += urlLength - 1;
                continue;
            }
            var prev = k > 0 ? text[k - 1] : ' ';
            var next = k + 1 < text.Length ? text[k + 1] : ' ';
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '[':
                case ']':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    if (IsAlnum(prev) && IsAlnum(next)) sb.Append(c);
                    else sb.Append("\\_");
                    break;
                case '~':
                    // "~~" is the strike marker, also when the second "~" comes from a marker next to the run.
                    if (prev == '~' || next == '~' || k == 0 || k == text.Length - 1) sb.Append("\\~");
                    else sb.Append(c);
                    break;
                case '<':
                    if (char.IsLetter(next) || next == '/' || next == '!') sb.Append("\\<");
                    else sb.Append(c);
                    break;
                case '|':
                    sb.Append(inTable ? "\\|" : "|");
                    break;
                case '\n':
                    sb.Append(inTable ? "<br>" : "\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ============================================================================ helpers

    /// <summary>The text of the inlines without any formatting.</summary>
    public static string PlainText(IEnumerable<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var run in inlines)
        {
            if (run.Kind == MdInlineKind.LineBreak) sb.Append(' ');
            else if (run.Kind == MdInlineKind.Text) sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>All the text of a document, one line per block (for search, word count and previews).</summary>
    public static string PlainText(IEnumerable<MdBlock> blocks)
    {
        var sb = new StringBuilder();
        void Walk(IEnumerable<MdBlock> list)
        {
            foreach (var block in list)
            {
                switch (block)
                {
                    case MdParagraph p: sb.AppendLine(PlainText(p.Inlines)); break;
                    case MdHeading h: sb.AppendLine(PlainText(h.Inlines)); break;
                    case MdCodeBlock c: sb.AppendLine(c.Code); break;
                    case MdQuote q: Walk(q.Blocks); break;
                    case MdList l:
                        foreach (var item in l.Items) Walk(item.Blocks);
                        break;
                    case MdTable t:
                        sb.AppendLine(string.Join(" ", t.Header.Select(PlainText)));
                        foreach (var row in t.Rows) sb.AppendLine(string.Join(" ", row.Select(PlainText)));
                        break;
                }
            }
        }
        Walk(blocks);
        return sb.ToString();
    }

    public static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            var isWord = char.IsLetterOrDigit(ch) || ch == '\'' || ch == '’';
            if (isWord && !inWord) count++;
            inWord = isWord;
        }
        return count;
    }

    /// <summary>The headings of a markdown text, with the line they are on.</summary>
    public static List<MdOutlineItem> Outline(string? text)
    {
        var items = new List<MdOutlineItem>();
        var lines = SplitLines(text);
        var inFence = false;
        var fenceChar = '`';
        var first = 0;
        if (lines.Count > 1 && lines[0].TrimEnd() == "---")
        {
            // front matter is not content
            var end = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
            if (end > 0) first = end + 1;
        }
        for (var i = first; i < lines.Count; i++)
        {
            var fence = FenceRx().Match(lines[i]);
            if (fence.Success && (!inFence || lines[i].Trim()[0] == fenceChar))
            {
                if (!inFence) fenceChar = fence.Groups[1].Value[0];
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var heading = HeadingRx().Match(lines[i]);
            if (heading.Success)
            {
                items.Add(new MdOutlineItem
                {
                    Level = heading.Groups[1].Length,
                    Text = PlainText(ParseInline(heading.Groups[2].Value.Trim())),
                    Index = items.Count,
                    Line = i
                });
                continue;
            }

            // setext headings
            if (i > first && !IsBlank(lines[i - 1]) && !HeadingRx().IsMatch(lines[i - 1]) && !ListRx().IsMatch(lines[i - 1]) &&
                !QuoteRx().IsMatch(lines[i - 1]) && !lines[i - 1].Contains('|'))
            {
                var t = lines[i].Trim();
                var level = t.Length > 0 && t.All(c => c == '=') ? 1 : t.Length > 1 && t.All(c => c == '-') ? 2 : 0;
                if (level > 0)
                    items.Add(new MdOutlineItem { Level = level, Text = PlainText(ParseInline(lines[i - 1].Trim())), Index = items.Count, Line = i - 1 });
            }
        }
        return items;
    }

    [GeneratedRegex(@"^ {0,3}\[[^\]\n]+\]:\s+\S")] private static partial Regex ReferenceDefinitionRx();
    [GeneratedRegex(@"\[\^[^\]\s]+\]")] private static partial Regex FootnoteRx();
    [GeneratedRegex(@"</?([A-Za-z][A-Za-z0-9]*)(?:\s[^<>]*)?/?>|<!--")] private static partial Regex HtmlTagRx();

    private static readonly HashSet<string> HtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "audio", "b", "blockquote", "br", "center", "code", "dd", "del", "details", "div", "dl", "dt", "em",
        "figure", "figcaption", "font", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "iframe", "img", "ins", "kbd", "li",
        "mark", "ol", "p", "picture", "pre", "s", "script", "section", "small", "source", "span", "strong", "style", "sub",
        "summary", "sup", "svg", "table", "tbody", "td", "th", "thead", "tr", "u", "ul", "video"
    };
    [GeneratedRegex(@"(`+)(?:(?!\1).)+?\1")] private static partial Regex CodeSpanRx();

    /// <summary>
    /// Tells whether a markdown text uses constructs that the formatted view cannot represent
    /// (raw HTML, reference-style links and their definitions, footnotes). Editing such a document in the
    /// formatted view would rewrite them as plain text, so the app keeps that view read-only for it.
    /// Returns a short description of what was found, or null.
    /// </summary>
    public static string? UnsupportedSyntax(string? text)
    {
        var lines = SplitLines(text);
        var inFence = false;
        var fenceChar = '`';
        foreach (var line in lines)
        {
            var fence = FenceRx().Match(line);
            if (fence.Success && (!inFence || line.Trim()[0] == fenceChar))
            {
                if (!inFence) fenceChar = fence.Groups[1].Value[0];
                inFence = !inFence;
                continue;
            }
            if (inFence || Indent(line) >= 4) continue;

            var clean = CodeSpanRx().Replace(line, "").Replace("\\<", "").Replace("\\[", "");
            if (FootnoteRx().IsMatch(clean)) return "footnotes";
            if (ReferenceDefinitionRx().IsMatch(clean)) return "reference-style links";
            foreach (Match tag in HtmlTagRx().Matches(clean))
            {
                if (tag.Value == "<!--") return "HTML comments";
                // Only real HTML elements: "List<string>" in a sentence is just text.
                if (HtmlTags.Contains(tag.Groups[1].Value) && !tag.Groups[1].Value.Equals("br", StringComparison.OrdinalIgnoreCase)) return "HTML";
            }
        }
        return null;
    }

    // ============================================================================ HTML export

    public static string ToHtml(IEnumerable<MdBlock> blocks, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(Html(title)).Append("</title>\n<style>\n")
          .Append("body{font-family:'Segoe UI',Arial,sans-serif;max-width:820px;margin:40px auto;padding:0 20px;line-height:1.6;color:#1b2733}\n")
          .Append("h1,h2,h3,h4,h5,h6{color:#16324f;line-height:1.25}\nh1{border-bottom:1px solid #d5dbe1;padding-bottom:.3em}\n")
          .Append("code{font-family:Consolas,monospace;background:#eef1f4;padding:.1em .35em;border-radius:4px}\n")
          .Append("pre{background:#eef1f4;padding:12px 14px;border-radius:8px;overflow:auto}\npre code{background:none;padding:0}\n")
          .Append("blockquote{margin:0;padding:2px 14px;border-left:4px solid #1f6f8b;color:#66727f}\n")
          .Append("table{border-collapse:collapse}\nth,td{border:1px solid #d5dbe1;padding:6px 10px}\nth{background:#eef1f4}\n")
          .Append("a{color:#1f6f8b}\nimg{max-width:100%}\nhr{border:0;border-top:1px solid #d5dbe1}\nul.tasks{list-style:none;padding-left:1.2em}\n")
          .Append("</style>\n</head>\n<body>\n");
        HtmlBlocks(blocks, sb);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void HtmlBlocks(IEnumerable<MdBlock> blocks, StringBuilder sb, bool tight = false)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading h:
                    sb.Append($"<h{h.Level}>").Append(HtmlInline(h.Inlines)).Append($"</h{h.Level}>\n");
                    break;
                case MdParagraph p:
                    if (tight) sb.Append(HtmlInline(p.Inlines)).Append('\n');
                    else sb.Append("<p>").Append(HtmlInline(p.Inlines)).Append("</p>\n");
                    break;
                case MdCodeBlock c:
                    sb.Append("<pre><code>").Append(Html(c.Code)).Append("</code></pre>\n");
                    break;
                case MdRule:
                    sb.Append("<hr>\n");
                    break;
                case MdQuote q:
                    sb.Append("<blockquote>\n");
                    HtmlBlocks(q.Blocks, sb);
                    sb.Append("</blockquote>\n");
                    break;
                case MdList l:
                    var tag = l.Ordered ? "ol" : "ul";
                    var isTasks = l.Items.Count > 0 && l.Items.All(i => i.Checked != null);
                    sb.Append('<').Append(tag);
                    if (isTasks) sb.Append(" class=\"tasks\"");
                    if (l.Ordered && l.Start != 1) sb.Append($" start=\"{l.Start}\"");
                    sb.Append(">\n");
                    foreach (var item in l.Items)
                    {
                        sb.Append("<li>");
                        if (item.Checked != null)
                            sb.Append("<input type=\"checkbox\" disabled").Append(item.Checked.Value ? " checked" : "").Append("> ");
                        HtmlBlocks(item.Blocks, sb, tight: true);
                        sb.Append("</li>\n");
                    }
                    sb.Append("</").Append(tag).Append(">\n");
                    break;
                case MdTable t:
                    sb.Append("<table>\n<thead><tr>");
                    for (var c = 0; c < t.Header.Count; c++) sb.Append("<th").Append(AlignAttr(t, c)).Append('>').Append(HtmlInline(t.Header[c])).Append("</th>");
                    sb.Append("</tr></thead>\n<tbody>\n");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        for (var c = 0; c < row.Count; c++) sb.Append("<td").Append(AlignAttr(t, c)).Append('>').Append(HtmlInline(row[c])).Append("</td>");
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</tbody>\n</table>\n");
                    break;
            }
        }
    }

    private static string AlignAttr(MdTable table, int column) =>
        (column < table.Align.Count ? table.Align[column] : MdAlign.None) switch
        {
            MdAlign.Left => " style=\"text-align:left\"",
            MdAlign.Center => " style=\"text-align:center\"",
            MdAlign.Right => " style=\"text-align:right\"",
            _ => ""
        };

    private static string HtmlInline(IEnumerable<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var run in inlines)
        {
            if (run.Kind == MdInlineKind.LineBreak)
            {
                sb.Append("<br>\n");
                continue;
            }

            string body;
            if (run.Kind == MdInlineKind.Image)
            {
                body = $"<img src=\"{Html(run.Source ?? "")}\" alt=\"{Html(run.Text)}\">";
            }
            else
            {
                body = Html(run.Text);
                if (run.Code) body = "<code>" + body + "</code>";
                if (run.Strike) body = "<del>" + body + "</del>";
                if (run.Italic) body = "<em>" + body + "</em>";
                if (run.Bold) body = "<strong>" + body + "</strong>";
            }

            if (run.Href != null)
            {
                var href = run.IsWiki ? Uri.EscapeDataString(run.Href.Split('#')[0]) + ".html" : run.Href;
                body = $"<a href=\"{Html(href)}\"{(string.IsNullOrEmpty(run.Title) ? "" : $" title=\"{Html(run.Title)}\"")}>{body}</a>";
            }
            sb.Append(body);
        }
        return sb.ToString();
    }
}
