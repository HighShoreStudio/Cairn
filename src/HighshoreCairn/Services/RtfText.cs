using System.Globalization;
using System.Text;

namespace HighshoreCairn.Services;

/// <summary>
/// Extracts the plain text of a rich text (.rtf) document without any UI library: used to search
/// the documentation and to count words. Formatting, pictures and tables borders are dropped.
/// </summary>
public static class RtfText
{
    // Groups that never contain text of the document.
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl", "headerr",
        "footerl", "footerr", "footnote", "themedata", "colorschememapping", "datastore", "latentstyles",
        "generator", "listtable", "listoverridetable", "rsidtbl", "xmlnstbl", "mmathPr", "fldinst", "filetbl",
        "revtbl", "pgdsctbl", "listtext", "pntext", "bkmkstart", "bkmkend"
    };

    private static Encoding? _ansi;

    /// <summary>The Windows "ANSI" code page that RTF files without Unicode escapes are written in.</summary>
    private static Encoding Ansi
    {
        get
        {
            if (_ansi != null) return _ansi;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _ansi = Encoding.GetEncoding(1252);
            }
            catch { _ansi = Encoding.Latin1; }
            return _ansi;
        }
    }

    /// <summary>
    /// The bytes of an RTF text, as the RTF reader of WPF wants them. RTF is a 7-bit format; the rare
    /// files that contain raw 8-bit characters use the ANSI code page.
    /// </summary>
    public static byte[] ToBytes(string rtf) => Ansi.GetBytes(rtf ?? "");

    public static string FromBytes(byte[] bytes) => Ansi.GetString(bytes);

    public static bool LooksLikeRtf(string? text) => text != null && text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

    public static string ToPlain(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        if (!LooksLikeRtf(rtf)) return rtf;

        var sb = new StringBuilder(rtf.Length / 2);
        var skipStack = new Stack<bool>();
        var skip = false;          // inside a group without document text
        var unicodeSkip = 1;       // \ucN: fallback characters that follow a \u escape
        var pendingSkip = 0;
        var ansi = Ansi;

        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    skipStack.Push(skip);
                    i++;
                    break;
                case '}':
                    skip = skipStack.Count > 0 && skipStack.Pop();
                    i++;
                    break;
                case '\r':
                case '\n':
                    i++;
                    break;
                case '\\':
                    i++;
                    if (i >= rtf.Length) break;
                    var next = rtf[i];
                    if (next is '\\' or '{' or '}')
                    {
                        if (pendingSkip > 0) pendingSkip--;
                        else if (!skip) sb.Append(next);
                        i++;
                    }
                    else if (next == '*')
                    {
                        skip = true;     // {\*\destination ...}: an optional group, never text
                        i++;
                    }
                    else if (next == '\'')
                    {
                        if (i + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        {
                            if (pendingSkip > 0) pendingSkip--;
                            else if (!skip) sb.Append(ansi.GetString(new[] { b }));
                        }
                        i += 3;
                    }
                    else if (next == '~') { if (!skip) sb.Append(' '); i++; }
                    else if (next == '-') { i++; }
                    else if (next == '_') { if (!skip) sb.Append('-'); i++; }
                    else if (next is '\r' or '\n') { if (!skip) sb.Append('\n'); i++; }
                    else if (char.IsAsciiLetter(next))
                    {
                        var start = i;
                        while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
                        var word = rtf[start..i];
                        var numberStart = i;
                        if (i < rtf.Length && rtf[i] == '-') i++;
                        while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                        var hasNumber = i > numberStart && char.IsDigit(rtf[i - 1]);
                        var number = hasNumber ? int.Parse(rtf.AsSpan(numberStart, i - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
                        if (!hasNumber) i = numberStart;
                        if (i < rtf.Length && rtf[i] == ' ') i++;   // the space that ends a control word

                        if (SkippedDestinations.Contains(word)) { skip = true; break; }
                        if (skip) break;
                        switch (word)
                        {
                            case "par": case "line": case "sect": case "page": case "row": sb.Append('\n'); break;
                            case "tab": case "cell": sb.Append('\t'); break;
                            case "emdash": sb.Append('—'); break;
                            case "endash": sb.Append('–'); break;
                            case "bullet": sb.Append('•'); break;
                            case "lquote": sb.Append('‘'); break;
                            case "rquote": sb.Append('’'); break;
                            case "ldblquote": sb.Append('“'); break;
                            case "rdblquote": sb.Append('”'); break;
                            case "uc": unicodeSkip = Math.Max(0, number); break;
                            case "u":
                                sb.Append((char)(number < 0 ? number + 65536 : number));
                                pendingSkip = unicodeSkip;
                                break;
                        }
                    }
                    else i++;
                    break;
                default:
                    if (pendingSkip > 0) pendingSkip--;
                    else if (!skip) sb.Append(c);
                    i++;
                    break;
            }
        }

        // Tidy up: no trailing spaces, at most one empty line in a row.
        var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd()).ToList();
        var result = new StringBuilder();
        var empty = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (++empty > 1) continue;
            }
            else empty = 0;
            result.Append(line).Append('\n');
        }
        var text = result.ToString().Trim('\n');
        return text.Length > 0 ? text + "\n" : "";
    }
}
