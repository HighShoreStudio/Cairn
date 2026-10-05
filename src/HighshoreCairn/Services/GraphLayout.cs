namespace HighshoreCairn.Services;

/// <summary>A node of a graph view, with its position in the layout.</summary>
public class GraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Nodes of the same group are drawn with the same color.</summary>
    public string Group { get; set; } = "";
    /// <summary>Drawn dimmed (for example a document that is referenced but does not exist).</summary>
    public bool IsGhost { get; set; }
    /// <summary>Optional color (hex) that replaces the color of the group.</summary>
    public string? Color { get; set; }
    public int Degree { get; set; }
    /// <summary>A favorite: drawn with a star.</summary>
    public bool Starred { get; set; }
    /// <summary>The item has references (to tasks or documents): drawn with a dot in the middle.</summary>
    public bool HasDot { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    /// <summary>Held in place (while the user drags it).</summary>
    public bool Pinned { get; set; }

    /// <summary>The more connections, the bigger the node.</summary>
    public double Radius => 5 + Math.Min(13, Math.Sqrt(Degree) * 3.0);

    public List<GraphNode> Neighbors { get; } = new();
}

public class GraphEdge
{
    public GraphEdge(GraphNode a, GraphNode b)
    {
        A = a;
        B = b;
    }

    public GraphNode A { get; }
    public GraphNode B { get; }
}

/// <summary>
/// Force-directed graph layout (the kind used by the Obsidian graph view): nodes repel each other,
/// edges pull the connected nodes together like springs and a weak gravity keeps everything around
/// the origin. Pure math: the control that draws it only calls <see cref="Step"/> on a timer.
/// </summary>
public class GraphModel
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();

    /// <summary>"Temperature" of the simulation: 1 = just started, 0 = at rest.</summary>
    public double Alpha { get; set; } = 1;

    public const double Repulsion = 5200;
    public const double SpringLength = 95;
    public const double SpringStrength = 0.045;
    public const double Gravity = 0.012;
    public const double Damping = 0.78;
    public const double MinAlpha = 0.004;

    public bool IsSettled => Alpha < MinAlpha;

    public GraphNode Add(string id, string label, string group = "", bool isGhost = false, string? color = null)
    {
        var node = new GraphNode { Id = id, Label = label, Group = group, IsGhost = isGhost, Color = color };
        Nodes.Add(node);
        return node;
    }

    public GraphNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public bool Connect(string a, string b)
    {
        var na = Find(a);
        var nb = Find(b);
        if (na is null || nb is null || na == nb || na.Neighbors.Contains(nb)) return false;
        Edges.Add(new GraphEdge(na, nb));
        na.Neighbors.Add(nb);
        nb.Neighbors.Add(na);
        na.Degree = na.Neighbors.Count;
        nb.Degree = nb.Neighbors.Count;
        return true;
    }

    public static GraphModel FromDocs(DocGraph graph)
    {
        var model = new GraphModel();
        foreach (var node in graph.Nodes) model.Add(node.Id, node.Label, node.Group, node.IsGhost);
        foreach (var edge in graph.Edges) model.Connect(edge.From, edge.To);
        model.Seed();
        return model;
    }

    /// <summary>
    /// Starting positions: a sunflower spiral, the most connected nodes at the center.
    /// Deterministic, so the same graph always opens with the same shape.
    /// </summary>
    public void Seed()
    {
        var ordered = Nodes.OrderByDescending(n => n.Degree).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        const double golden = 2.399963229728653; // golden angle in radians
        for (var i = 0; i < ordered.Count; i++)
        {
            var radius = 34 * Math.Sqrt(i + 0.5);
            ordered[i].X = radius * Math.Cos(i * golden);
            ordered[i].Y = radius * Math.Sin(i * golden);
            ordered[i].Vx = ordered[i].Vy = 0;
        }
        Alpha = 1;
    }

    /// <summary>Wakes the simulation up (after a node was dragged or the graph changed).</summary>
    public void Reheat(double alpha = 0.35) => Alpha = Math.Max(Alpha, alpha);

    /// <summary>Advances the simulation by one tick. Returns false when the layout is at rest.</summary>
    public bool Step()
    {
        if (IsSettled || Nodes.Count == 0) return false;
        var n = Nodes.Count;

        // Repulsion between every pair of nodes.
        for (var i = 0; i < n; i++)
        {
            var a = Nodes[i];
            for (var j = i + 1; j < n; j++)
            {
                var b = Nodes[j];
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 < 0.01)
                {
                    // Two nodes on the same spot: push them apart in a fixed direction.
                    dx = 0.1 * ((i + j) % 2 == 0 ? 1 : -1);
                    dy = 0.1;
                    d2 = dx * dx + dy * dy;
                }
                var d = Math.Sqrt(d2);
                var force = Repulsion / d2 * Alpha;
                var fx = dx / d * force;
                var fy = dy / d * force;
                a.Vx += fx;
                a.Vy += fy;
                b.Vx -= fx;
                b.Vy -= fy;
            }
        }

        // Springs along the edges.
        foreach (var edge in Edges)
        {
            var dx = edge.B.X - edge.A.X;
            var dy = edge.B.Y - edge.A.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 0.01) continue;
            var length = SpringLength + edge.A.Radius + edge.B.Radius;
            var force = (d - length) * SpringStrength * Alpha;
            var fx = dx / d * force;
            var fy = dy / d * force;
            edge.A.Vx += fx;
            edge.A.Vy += fy;
            edge.B.Vx -= fx;
            edge.B.Vy -= fy;
        }

        // Gravity toward the center, then move.
        foreach (var node in Nodes)
        {
            node.Vx -= node.X * Gravity * Alpha;
            node.Vy -= node.Y * Gravity * Alpha;
            node.Vx *= Damping;
            node.Vy *= Damping;
            if (node.Pinned)
            {
                node.Vx = node.Vy = 0;
                continue;
            }
            var speed = Math.Sqrt(node.Vx * node.Vx + node.Vy * node.Vy);
            if (speed > 40)
            {
                node.Vx *= 40 / speed;
                node.Vy *= 40 / speed;
            }
            node.X += node.Vx;
            node.Y += node.Vy;
        }

        Alpha *= 0.985;
        return !IsSettled;
    }

    /// <summary>Runs the simulation without drawing (used to open the view already laid out).</summary>
    public void Run(int ticks)
    {
        for (var i = 0; i < ticks && Step(); i++) { }
    }

    /// <summary>Bounding box of the nodes (left, top, right, bottom), including their size.</summary>
    public (double Left, double Top, double Right, double Bottom) Bounds()
    {
        if (Nodes.Count == 0) return (-100, -100, 100, 100);
        return (Nodes.Min(n => n.X - n.Radius), Nodes.Min(n => n.Y - n.Radius),
                Nodes.Max(n => n.X + n.Radius), Nodes.Max(n => n.Y + n.Radius));
    }

    /// <summary>The node under a point (the top-most one), or null.</summary>
    public GraphNode? HitTest(double x, double y, double tolerance = 4)
    {
        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            var node = Nodes[i];
            var dx = x - node.X;
            var dy = y - node.Y;
            var r = node.Radius + tolerance;
            if (dx * dx + dy * dy <= r * r) return node;
        }
        return null;
    }
}

/// <summary>Builds the graph of a whiteboard: the elements linked by connectors.</summary>
public static class WhiteboardGraph
{
    public static GraphModel Build(HighshoreCairn.Models.WhiteboardData data, ICollection<string>? alsoShow = null)
    {
        var model = new GraphModel();
        var linked = new HashSet<string>();
        foreach (var connector in data.Connectors)
        {
            linked.Add(connector.FromId);
            linked.Add(connector.ToId);
        }

        var counters = new Dictionary<string, int>();
        foreach (var element in data.Elements)
        {
            // The label counter runs over every element, so "Rectangle 2" is the same one whatever is shown.
            var label = LabelOf(element, counters);
            var shown = linked.Contains(element.Id) || element.Favorite || element.HasRefs || alsoShow?.Contains(element.Id) == true;
            if (!shown) continue;
            var node = model.Add(element.Id, label, element.Kind.ToString(), false, element.Fill ?? element.Stroke);
            node.Starred = element.Favorite;
            node.HasDot = element.HasRefs;
        }
        foreach (var connector in data.Connectors) model.Connect(connector.FromId, connector.ToId);
        model.Seed();
        return model;
    }

    /// <summary>The text of the element when it has one, otherwise its kind with a counter ("Rectangle 2").</summary>
    public static string LabelOf(HighshoreCairn.Models.WbElement element, Dictionary<string, int>? counters = null)
    {
        var text = FirstText(element);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var line = text.Trim().Split('\n')[0].Trim();
            return line.Length > 36 ? line[..36] + "…" : line;
        }
        var kind = element.Kind switch
        {
            HighshoreCairn.Models.WbKind.Stroke => "Drawing",
            HighshoreCairn.Models.WbKind.Polyline => element.Closed ? "Shape" : "Polyline",
            _ => element.Kind.ToString()
        };
        if (counters is null) return kind;
        counters[kind] = counters.GetValueOrDefault(kind) + 1;
        return $"{kind} {counters[kind]}";
    }

    private static string? FirstText(HighshoreCairn.Models.WbElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Text)) return element.Text;
        if (element.Children is null) return null;
        foreach (var child in element.Children)
            if (FirstText(child) is { } text) return text;
        return null;
    }
}
