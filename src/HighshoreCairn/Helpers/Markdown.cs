using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HighshoreCairn.Helpers;

/// <summary>
/// Minimal markdown renderer for a TextBlock:
///   &lt;TextBlock TextWrapping="Wrap" h:Markdown.Text="{Binding Description}" /&gt;
/// Supported: # headings, - bullets, - [ ] / - [x] tasks, **bold**, *italic*, `code`,
/// [links](https://...) and bare http(s) URLs.
/// </summary>
public static class Markdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Markdown), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static readonly Regex InlinePattern = new(
        @"(?<bold>\*\*(?<b>.+?)\*\*)|(?<italic>\*(?<i>[^*\s][^*]*?)\*)|(?<code>`(?<c>[^`]+)`)|(?<link>\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\))|(?<url>https?://[^\s<>]+)",
        RegexOptions.Compiled);

    private static readonly FontFamily CodeFont = new("Consolas");

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var inline in Parse(e.NewValue as string ?? "")) block.Inlines.Add(inline);
    }

    public static IEnumerable<Inline> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) yield return new LineBreak();
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("### ")) yield return Heading(trimmed[4..], 14);
            else if (trimmed.StartsWith("## ")) yield return Heading(trimmed[3..], 15);
            else if (trimmed.StartsWith("# ")) yield return Heading(trimmed[2..], 17);
            else if (trimmed.StartsWith("- [ ] ")) yield return Line("☐  ", trimmed[6..]);
            else if (trimmed.StartsWith("- [x] ") || trimmed.StartsWith("- [X] ")) yield return Line("☑  ", trimmed[6..]);
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) yield return Line("•  ", trimmed[2..]);
            else yield return Line("", line);
        }
    }

    private static Inline Heading(string text, double size)
    {
        var span = new Span { FontWeight = FontWeights.Bold, FontSize = size };
        foreach (var inline in ParseInline(text)) span.Inlines.Add(inline);
        return span;
    }

    private static Inline Line(string prefix, string text)
    {
        var span = new Span();
        if (prefix.Length > 0) span.Inlines.Add(new Run(prefix));
        foreach (var inline in ParseInline(text)) span.Inlines.Add(inline);
        return span;
    }

    private static IEnumerable<Inline> ParseInline(string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position) yield return new Run(text[position..match.Index]);
            position = match.Index + match.Length;

            if (match.Groups["bold"].Success)
                yield return new Run(match.Groups["b"].Value) { FontWeight = FontWeights.Bold };
            else if (match.Groups["italic"].Success)
                yield return new Run(match.Groups["i"].Value) { FontStyle = FontStyles.Italic };
            else if (match.Groups["code"].Success)
                yield return Code(match.Groups["c"].Value);
            else if (match.Groups["link"].Success)
                yield return Link(match.Groups["lt"].Value, match.Groups["lu"].Value);
            else
                yield return Link(match.Value, match.Value);
        }
        if (position < text.Length) yield return new Run(text[position..]);
    }

    private static Inline Code(string text)
    {
        var run = new Run(text) { FontFamily = CodeFont };
        run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Chip"); // follows the theme
        return run;
    }

    private static Inline Link(string text, string url)
    {
        // Only web and mail links are clickable: project files can come from other people (git).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
            return new Run(text);

        var link = new Hyperlink(new Run(text)) { NavigateUri = uri, ToolTip = url };
        link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Link"); // follows the theme
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* no browser available */ }
            e.Handled = true;
        };
        return link;
    }
}
