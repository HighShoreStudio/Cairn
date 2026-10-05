using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HighshoreCairn.Services;

/// <summary>One drawing instruction of an icon: an outline to fill and/or to stroke.</summary>
public class SvgShape
{
    /// <summary>
    /// Path data with absolute commands only (M, L, C, Q, A, Z), numbers separated by spaces:
    /// the syntax of SVG paths and of WPF geometries is the same for these commands.
    /// </summary>
    public string Path { get; set; } = "";
    public bool Fill { get; set; }
    public bool EvenOdd { get; set; }
    public bool Stroke { get; set; }
    public double StrokeWidth { get; set; }
    public bool RoundCap { get; set; }
    public bool RoundJoin { get; set; }
}

/// <summary>A vector icon: shapes inside a Width x Height box, all drawn with one color.</summary>
public class SvgIcon
{
    public double Width { get; set; } = 512;
    public double Height { get; set; } = 512;
    public List<SvgShape> Shapes { get; } = new();
}

/// <summary>
/// A small reader for simple, single-color SVG icons (the subset used by the Ionicons set: path, circle,
/// ellipse, rect, line, polyline, polygon, groups and rotate/translate/scale transforms).
/// Gradients, text, clipping and CSS classes are not supported: such files are rejected or drawn partially.
/// </summary>
public static class SvgParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        /// <summary>this * other: other is applied first.</summary>
        public Matrix Then(Matrix o) => new(
            A * o.A + C * o.B, B * o.A + D * o.B,
            A * o.C + C * o.D, B * o.C + D * o.D,
            A * o.E + C * o.F + E, B * o.E + D * o.F + F);
        /// <summary>Rotation part in degrees and uniform scale (the icons only use rotations, moves and uniform scales).</summary>
        public double Angle => Math.Atan2(B, A) * 180 / Math.PI;
        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
        public bool Mirrors => A * D - B * C < 0;
    }

    private class Style
    {
        public string Fill = "black";
        public string Stroke = "none";
        public double StrokeWidth = 1;
        public string Cap = "butt";
        public string Join = "miter";
        public string FillRule = "nonzero";
        public Style Clone() => (Style)MemberwiseClone();
    }

    public static SvgIcon Parse(string svg)
    {
        var doc = XDocument.Parse(svg);
        var root = doc.Root ?? throw new FormatException("Not an SVG file.");
        if (root.Name.LocalName != "svg") throw new FormatException("Not an SVG file.");

        var icon = new SvgIcon();
        var offset = Matrix.Identity;
        var viewBox = Numbers((string?)root.Attribute("viewBox") ?? "");
        if (viewBox.Count == 4 && viewBox[2] > 0 && viewBox[3] > 0)
        {
            icon.Width = viewBox[2];
            icon.Height = viewBox[3];
            if (viewBox[0] != 0 || viewBox[1] != 0) offset = new Matrix(1, 0, 0, 1, -viewBox[0], -viewBox[1]);
        }
        else
        {
            var w = Length((string?)root.Attribute("width"));
            var h = Length((string?)root.Attribute("height"));
            if (w > 0 && h > 0)
            {
                icon.Width = w;
                icon.Height = h;
            }
        }

        var style = new Style();
        ReadStyle(root, style);
        Walk(root, style, offset, icon, 0);
        return icon;
    }

    private static void Walk(XElement parent, Style inherited, Matrix matrix, SvgIcon icon, int depth)
    {
        if (depth > 16) return;
        foreach (var element in parent.Elements())
        {
            var name = element.Name.LocalName;
            if (name is "defs" or "title" or "desc" or "style" or "metadata" or "clipPath" or "mask" or "symbol") continue;

            var style = inherited.Clone();
            ReadStyle(element, style);
            var local = matrix.Then(Transform((string?)element.Attribute("transform")));

            if (name == "g")
            {
                Walk(element, style, local, icon, depth + 1);
                continue;
            }

            var commands = name switch
            {
                "path" => PathCommands((string?)element.Attribute("d") ?? ""),
                "circle" => Ellipse(Num(element, "cx"), Num(element, "cy"), Num(element, "r"), Num(element, "r")),
                "ellipse" => Ellipse(Num(element, "cx"), Num(element, "cy"), Num(element, "rx"), Num(element, "ry")),
                "rect" => Rect(element),
                "line" => new List<Cmd>
                {
                    new('M', Num(element, "x1"), Num(element, "y1")),
                    new('L', Num(element, "x2"), Num(element, "y2"))
                },
                "polyline" => Poly((string?)element.Attribute("points") ?? "", close: false),
                "polygon" => Poly((string?)element.Attribute("points") ?? "", close: true),
                _ => null
            };
            if (commands is null || commands.Count == 0) continue;

            var fill = style.Fill != "none" && style.Fill != "transparent";
            var stroke = style.Stroke != "none" && style.Stroke != "transparent" && style.StrokeWidth > 0;
            if (!fill && !stroke) continue;
            // A line has no inside: only its stroke can be drawn.
            if (name == "line" && !stroke) continue;

            icon.Shapes.Add(new SvgShape
            {
                Path = Write(commands, local),
                Fill = fill && name != "line",
                EvenOdd = style.FillRule == "evenodd",
                Stroke = stroke,
                StrokeWidth = style.StrokeWidth * local.Scale,
                RoundCap = style.Cap == "round",
                RoundJoin = style.Join == "round"
            });
        }
    }

    // ------------------------------------------------------------------ style

    private static void ReadStyle(XElement element, Style style)
    {
        foreach (var attribute in element.Attributes())
            Apply(style, attribute.Name.LocalName, attribute.Value);
        var inline = (string?)element.Attribute("style");
        if (string.IsNullOrWhiteSpace(inline)) return;
        foreach (var declaration in inline.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            Apply(style, declaration[..colon].Trim(), declaration[(colon + 1)..].Trim());
        }
    }

    private static void Apply(Style style, string name, string value)
    {
        value = value.Trim();
        switch (name)
        {
            case "fill": style.Fill = value.ToLowerInvariant(); break;
            case "stroke": style.Stroke = value.ToLowerInvariant(); break;
            case "stroke-width":
                var width = Length(value);
                if (!double.IsNaN(width)) style.StrokeWidth = width;
                break;
            case "stroke-linecap": style.Cap = value.ToLowerInvariant(); break;
            case "stroke-linejoin": style.Join = value.ToLowerInvariant(); break;
            case "fill-rule": style.FillRule = value.ToLowerInvariant(); break;
        }
    }

    /// <summary>"32", "32px", "1.5" -> number. NaN when it cannot be read.</summary>
    private static double Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        text = text.Trim();
        var end = text.Length;
        while (end > 0 && !char.IsDigit(text[end - 1]) && text[end - 1] != '.') end--;
        return double.TryParse(text.AsSpan(0, end), NumberStyles.Float, Inv, out var value) ? value : double.NaN;
    }

    private static double Num(XElement element, string name)
    {
        var value = Length((string?)element.Attribute(name));
        return double.IsNaN(value) ? 0 : value;
    }

    // ------------------------------------------------------------------ transforms

    private static Matrix Transform(string? text)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('(', i);
            if (open < 0) break;
            var close = text.IndexOf(')', open);
            if (close < 0) break;
            var name = text[i..open].Trim(' ', ',', '\t', '\n', '\r');
            var n = Numbers(text[(open + 1)..close]);
            Matrix m;
            switch (name)
            {
                case "translate" when n.Count >= 1:
                    m = new Matrix(1, 0, 0, 1, n[0], n.Count > 1 ? n[1] : 0);
                    break;
                case "scale" when n.Count >= 1:
                    m = new Matrix(n[0], 0, 0, n.Count > 1 ? n[1] : n[0], 0, 0);
                    break;
                case "rotate" when n.Count >= 1:
                    var radians = n[0] * Math.PI / 180;
                    var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
                    var (cx, cy) = n.Count >= 3 ? (n[1], n[2]) : (0d, 0d);
                    m = new Matrix(cos, sin, -sin, cos, cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
                    break;
                case "matrix" when n.Count == 6:
                    m = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]);
                    break;
                default:
                    throw new FormatException($"Unsupported transform '{name}'.");
            }
            result = result.Then(m);
            i = close + 1;
        }
        return result;
    }

    // ------------------------------------------------------------------ shapes

    /// <summary>A path command with absolute coordinates. Arc: Args = rx, ry, rotation, large, sweep, x, y.</summary>
    private readonly record struct Cmd(char Type, params double[] Args);

    private static List<Cmd> Ellipse(double cx, double cy, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0) return new List<Cmd>();
        return new List<Cmd>
        {
            new('M', cx - rx, cy),
            new('A', rx, ry, 0, 1, 0, cx + rx, cy),
            new('A', rx, ry, 0, 1, 0, cx - rx, cy),
            new('Z')
        };
    }

    private static List<Cmd> Rect(XElement element)
    {
        double x = Num(element, "x"), y = Num(element, "y"), w = Num(element, "width"), h = Num(element, "height");
        if (w <= 0 || h <= 0) return new List<Cmd>();
        var rx = Length((string?)element.Attribute("rx"));
        var ry = Length((string?)element.Attribute("ry"));
        if (double.IsNaN(rx)) rx = double.IsNaN(ry) ? 0 : ry;
        if (double.IsNaN(ry)) ry = rx;
        rx = Math.Min(Math.Max(rx, 0), w / 2);
        ry = Math.Min(Math.Max(ry, 0), h / 2);

        if (rx == 0 || ry == 0)
            return new List<Cmd> { new('M', x, y), new('L', x + w, y), new('L', x + w, y + h), new('L', x, y + h), new('Z') };

        return new List<Cmd>
        {
            new('M', x + rx, y),
            new('L', x + w - rx, y),
            new('A', rx, ry, 0, 0, 1, x + w, y + ry),
            new('L', x + w, y + h - ry),
            new('A', rx, ry, 0, 0, 1, x + w - rx, y + h),
            new('L', x + rx, y + h),
            new('A', rx, ry, 0, 0, 1, x, y + h - ry),
            new('L', x, y + ry),
            new('A', rx, ry, 0, 0, 1, x + rx, y),
            new('Z')
        };
    }

    private static List<Cmd> Poly(string points, bool close)
    {
        var n = Numbers(points);
        var result = new List<Cmd>();
        for (var i = 0; i + 1 < n.Count; i += 2) result.Add(new Cmd(i == 0 ? 'M' : 'L', n[i], n[i + 1]));
        if (close && result.Count > 1) result.Add(new Cmd('Z'));
        return result;
    }

    // ------------------------------------------------------------------ path data

    private ref struct Scanner
    {
        private readonly ReadOnlySpan<char> _s;
        private int _i;
        public Scanner(string text) { _s = text; _i = 0; }

        private void SkipSeparators()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }

        public bool AtEnd { get { SkipSeparators(); return _i >= _s.Length; } }

        /// <summary>The next character is a command letter.</summary>
        public bool TryCommand(out char command)
        {
            SkipSeparators();
            command = '\0';
            if (_i >= _s.Length) return false;
            var c = _s[_i];
            if (!char.IsLetter(c) || c is 'e' or 'E') return false;
            command = c;
            _i++;
            return true;
        }

        public bool HasNumber
        {
            get
            {
                SkipSeparators();
                if (_i >= _s.Length) return false;
                var c = _s[_i];
                return char.IsDigit(c) || c is '-' or '+' or '.';
            }
        }

        public double Number()
        {
            SkipSeparators();
            var start = _i;
            if (_i < _s.Length && _s[_i] is '-' or '+') _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i < _s.Length && _s[_i] is 'e' or 'E')
            {
                var mark = _i;
                _i++;
                if (_i < _s.Length && _s[_i] is '-' or '+') _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i]))
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                else
                    _i = mark;
            }
            if (!double.TryParse(_s[start.._i], NumberStyles.Float, Inv, out var value))
                throw new FormatException("Invalid number in path data.");
            return value;
        }

        /// <summary>Arc flags are single digits and may be written without separators ("a1 1 0 011 1").</summary>
        public double Flag()
        {
            SkipSeparators();
            if (_i >= _s.Length || _s[_i] is not ('0' or '1')) throw new FormatException("Invalid arc flag in path data.");
            return _s[_i++] == '1' ? 1 : 0;
        }
    }

    private static List<Cmd> PathCommands(string d)
    {
        var result = new List<Cmd>();
        var scanner = new Scanner(d);
        double x = 0, y = 0, startX = 0, startY = 0;
        double lastCx = 0, lastCy = 0;   // last control point (for S and T)
        var last = '\0';
        var command = '\0';

        while (!scanner.AtEnd)
        {
            if (scanner.TryCommand(out var next)) command = next;
            else if (command == '\0') throw new FormatException("Path data must start with a command.");
            else if (command is 'Z' or 'z') throw new FormatException("Unexpected number after a close command.");
            // After a move, extra coordinate pairs are lines.
            else if (command == 'M') command = 'L';
            else if (command == 'm') command = 'l';

            var relative = char.IsLower(command);
            var type = char.ToUpperInvariant(command);
            double ox = relative ? x : 0, oy = relative ? y : 0;

            switch (type)
            {
                case 'M':
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    startX = x; startY = y;
                    result.Add(new Cmd('M', x, y));
                    break;
                case 'L':
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    result.Add(new Cmd('L', x, y));
                    break;
                case 'H':
                    x = ox + scanner.Number();
                    result.Add(new Cmd('L', x, y));
                    break;
                case 'V':
                    y = oy + scanner.Number();
                    result.Add(new Cmd('L', x, y));
                    break;
                case 'C':
                {
                    double x1 = ox + scanner.Number(), y1 = oy + scanner.Number();
                    double x2 = ox + scanner.Number(), y2 = oy + scanner.Number();
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    result.Add(new Cmd('C', x1, y1, x2, y2, x, y));
                    lastCx = x2; lastCy = y2;
                    break;
                }
                case 'S':
                {
                    // The first control point mirrors the last one of the previous curve.
                    double x1 = last == 'C' ? 2 * x - lastCx : x, y1 = last == 'C' ? 2 * y - lastCy : y;
                    double x2 = ox + scanner.Number(), y2 = oy + scanner.Number();
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    result.Add(new Cmd('C', x1, y1, x2, y2, x, y));
                    lastCx = x2; lastCy = y2;
                    type = 'C';
                    break;
                }
                case 'Q':
                {
                    double x1 = ox + scanner.Number(), y1 = oy + scanner.Number();
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    result.Add(new Cmd('Q', x1, y1, x, y));
                    lastCx = x1; lastCy = y1;
                    break;
                }
                case 'T':
                {
                    double x1 = last == 'Q' ? 2 * x - lastCx : x, y1 = last == 'Q' ? 2 * y - lastCy : y;
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    result.Add(new Cmd('Q', x1, y1, x, y));
                    lastCx = x1; lastCy = y1;
                    type = 'Q';
                    break;
                }
                case 'A':
                {
                    double rx = Math.Abs(scanner.Number()), ry = Math.Abs(scanner.Number()), rotation = scanner.Number();
                    double large = scanner.Flag(), sweep = scanner.Flag();
                    x = ox + scanner.Number(); y = oy + scanner.Number();
                    // An arc with a zero radius is a straight line.
                    result.Add(rx == 0 || ry == 0 ? new Cmd('L', x, y) : new Cmd('A', rx, ry, rotation, large, sweep, x, y));
                    break;
                }
                case 'Z':
                    x = startX; y = startY;
                    result.Add(new Cmd('Z'));
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{command}'.");
            }
            last = type;
        }
        return result;
    }

    private static string Write(List<Cmd> commands, Matrix m)
    {
        var sb = new StringBuilder(commands.Count * 24);
        void Point(double px, double py)
        {
            var (tx, ty) = m.Apply(px, py);
            sb.Append(' ').Append(Format(tx)).Append(' ').Append(Format(ty));
        }

        foreach (var c in commands)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(c.Type);
            var a = c.Args;
            switch (c.Type)
            {
                case 'M':
                case 'L':
                    Point(a[0], a[1]);
                    break;
                case 'C':
                    Point(a[0], a[1]); Point(a[2], a[3]); Point(a[4], a[5]);
                    break;
                case 'Q':
                    Point(a[0], a[1]); Point(a[2], a[3]);
                    break;
                case 'A':
                    var sweep = m.Mirrors ? 1 - a[4] : a[4];
                    sb.Append(' ').Append(Format(a[0] * m.Scale)).Append(' ').Append(Format(a[1] * m.Scale))
                      .Append(' ').Append(Format(a[2] + m.Angle))
                      .Append(' ').Append(a[3] == 1 ? '1' : '0').Append(' ').Append(sweep == 1 ? '1' : '0');
                    Point(a[5], a[6]);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new FormatException("Invalid number in path data.");
        var text = value.ToString("0.###", Inv);
        return text == "-0" ? "0" : text;
    }

    private static List<double> Numbers(string text)
    {
        var result = new List<double>();
        var scanner = new Scanner(text);
        while (!scanner.AtEnd)
        {
            if (!scanner.HasNumber) throw new FormatException("A number was expected.");
            result.Add(scanner.Number());
        }
        return result;
    }
}

/// <summary>
/// The built-in icon set: a zip of SVG files (Ionicons, MIT license) embedded in the application.
/// The icons are read and parsed only when they are asked for.
/// </summary>
public class IconLibrary
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SvgIcon?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="zip">A zip archive with one "name.svg" entry per icon. null = an empty library.</param>
    public IconLibrary(Stream? zip)
    {
        if (zip is null) return;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            _files[entry.Name[..^4]] = memory.ToArray();
        }
        Names = _files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Names of the icons, sorted ("rocket", "rocket-outline", "rocket-sharp", ...).</summary>
    public IReadOnlyList<string> Names { get; } = Array.Empty<string>();

    public bool Contains(string name) => _files.ContainsKey(name);

    /// <summary>null when the icon does not exist or cannot be read.</summary>
    public SvgIcon? Get(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            SvgIcon? icon = null;
            if (_files.TryGetValue(name, out var bytes))
            {
                try { icon = SvgParser.Parse(Encoding.UTF8.GetString(bytes)); }
                catch { icon = null; }
            }
            _cache[name] = icon;
            return icon;
        }
    }

    /// <summary>"Outline", "Sharp" or "Filled", from the suffix of the name.</summary>
    public static string VariantOf(string name) =>
        name.EndsWith("-outline", StringComparison.OrdinalIgnoreCase) ? "Outline"
        : name.EndsWith("-sharp", StringComparison.OrdinalIgnoreCase) ? "Sharp"
        : "Filled";

    public const string BuiltInPrefix = "ion:";
    public const string FilePrefix = "file:";

    /// <summary>"ion:rocket" -> "rocket"; null for anything else.</summary>
    public static string? BuiltInName(string? icon) =>
        icon != null && icon.StartsWith(BuiltInPrefix, StringComparison.Ordinal) ? icon[BuiltInPrefix.Length..] : null;

    /// <summary>"file:logo.png" -> "logo.png"; null for anything else.</summary>
    public static string? FileName(string? icon) =>
        icon != null && icon.StartsWith(FilePrefix, StringComparison.Ordinal) ? icon[FilePrefix.Length..] : null;
}
