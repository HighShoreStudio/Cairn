using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighshoreCairn.Models;

public enum WbKind { Text, Stroke, Line, Polyline, Rectangle, Ellipse, Triangle, Polygon, Image, Group }

/// <summary>A 2D point (whiteboard units, or 0..1 when normalized inside an element box).</summary>
public readonly record struct WbPoint(double X, double Y)
{
    public static WbPoint operator +(WbPoint a, WbPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static WbPoint operator -(WbPoint a, WbPoint b) => new(a.X - b.X, a.Y - b.Y);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// An element of a whiteboard. Every element is a box (X, Y, Width, Height) rotated around its
/// center by <see cref="Rotation"/> degrees; what is drawn inside the box depends on <see cref="Kind"/>.
/// </summary>
public class WbElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public WbKind Kind { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; }

    /// <summary>Line / text color (hex). null = automatic: follows the theme (black on light, white on dark).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stroke { get; set; }

    public double StrokeWidth { get; set; } = 2;

    /// <summary>Fill color (hex). null = no fill.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fill { get; set; }

    /// <summary>For freehand strokes and polylines: the last point is joined to the first one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }

    /// <summary>Points of strokes / lines / polygons, normalized to the element box (0..1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(WbPointListConverter))]
    public List<WbPoint>? Points { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double FontSize { get; set; }

    /// <summary>File name inside the whiteboards/images folder of the project.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Image { get; set; }

    /// <summary>Group only: the merged elements, positioned inside a RefWidth x RefHeight space.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WbElement>? Children { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RefWidth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RefHeight { get; set; }

    /// <summary>Marked with the star: the node stands out in the graph view.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; set; }

    /// <summary>References: ids of the Kanban cards linked to this element (a card is linked to one element only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CardRefs { get; set; }

    /// <summary>References: paths (inside the docs folder) of the documents linked to this element.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DocRefs { get; set; }

    [JsonIgnore]
    public bool HasRefs => CardRefs is { Count: > 0 } || DocRefs is { Count: > 0 };

    [JsonIgnore]
    public WbPoint Center => new(X + Width / 2, Y + Height / 2);

    [JsonIgnore]
    public bool IsDrawing => Kind is not (WbKind.Text or WbKind.Image or WbKind.Group);
}

/// <summary>A line that links two elements and follows them when they move.</summary>
public class WbConnector
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public double Thickness { get; set; } = 1;

    /// <summary>Hex color. null = automatic (follows the theme).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    /// <summary>Draws an arrow head on the "to" side.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Arrow { get; set; }

    public bool Links(string a, string b) => (FromId == a && ToId == b) || (FromId == b && ToId == a);
}

/// <summary>Content of a whiteboards/*.json file. The list order of the elements is the z-order.</summary>
public class WhiteboardData
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Whiteboard";
    public List<WbElement> Elements { get; set; } = new();
    public List<WbConnector> Connectors { get; set; } = new();
}

/// <summary>
/// Writes a point list as one compact string ("0.25,0.5 0.3,0.61 ...") instead of one JSON
/// object per point: freehand strokes stay small and produce readable git diffs.
/// </summary>
public class WbPointListConverter : JsonConverter<List<WbPoint>>
{
    public override List<WbPoint> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<WbPoint>();
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var pair in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = pair.IndexOf(',');
            if (comma <= 0) continue;
            if (double.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                result.Add(new WbPoint(x, y));
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<WbPoint> value, JsonSerializerOptions options)
    {
        var sb = new StringBuilder(value.Count * 14);
        foreach (var p in value)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.X.ToString("0.####", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(p.Y.ToString("0.####", CultureInfo.InvariantCulture));
        }
        writer.WriteStringValue(sb.ToString());
    }
}
