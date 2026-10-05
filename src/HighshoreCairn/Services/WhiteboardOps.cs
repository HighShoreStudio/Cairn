using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>Axis-aligned rectangle in whiteboard units.</summary>
public readonly record struct WbRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsEmpty => Right < Left || Bottom < Top;

    public static WbRect Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public WbRect Union(WbRect o) => IsEmpty ? o : o.IsEmpty ? this
        : new WbRect(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    public bool Intersects(WbRect o) => !(o.Left > Right || o.Right < Left || o.Top > Bottom || o.Bottom < Top);
}

/// <summary>
/// Whiteboard logic without any UI dependency: geometry of rotated boxes, resizing,
/// grouping, connectors. The canvas (view) calls these and only takes care of drawing.
/// </summary>
public static class WhiteboardOps
{
    public const double MinSize = 4;

    // ------------------------------------------------------------------ geometry

    /// <summary>Rotates a vector by <paramref name="degrees"/> (clockwise on screen, like WPF).</summary>
    public static WbPoint Rotate(WbPoint v, double degrees)
    {
        var r = degrees * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        return new WbPoint(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    /// <summary>World position of a point given relative to the element center, along the element axes.</summary>
    public static WbPoint LocalToWorld(WbElement e, WbPoint local) => e.Center + Rotate(local, e.Rotation);

    /// <summary>World point → offset from the element center along the element (unrotated) axes.</summary>
    public static WbPoint WorldToLocal(WbElement e, WbPoint world) => Rotate(world - e.Center, -e.Rotation);

    /// <summary>The four corners in world coordinates: top-left, top-right, bottom-right, bottom-left.</summary>
    public static WbPoint[] Corners(WbElement e)
    {
        double hw = e.Width / 2, hh = e.Height / 2;
        return new[]
        {
            LocalToWorld(e, new WbPoint(-hw, -hh)),
            LocalToWorld(e, new WbPoint(hw, -hh)),
            LocalToWorld(e, new WbPoint(hw, hh)),
            LocalToWorld(e, new WbPoint(-hw, hh))
        };
    }

    /// <summary>Axis-aligned bounding box of the rotated element.</summary>
    public static WbRect Bounds(WbElement e)
    {
        var c = Corners(e);
        return new WbRect(c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
    }

    public static WbRect Bounds(IEnumerable<WbElement> elements)
    {
        var result = WbRect.Empty;
        foreach (var e in elements) result = result.Union(Bounds(e));
        return result;
    }

    public static bool Contains(WbElement e, WbPoint world)
    {
        var local = WorldToLocal(e, world);
        return Math.Abs(local.X) <= e.Width / 2 && Math.Abs(local.Y) <= e.Height / 2;
    }

    /// <summary>
    /// Point on the outline of the element in the direction of <paramref name="toward"/>:
    /// where a connector coming from the center leaves the element.
    /// </summary>
    public static WbPoint EdgePoint(WbElement e, WbPoint toward)
    {
        var d = WorldToLocal(e, toward);
        double hw = Math.Max(e.Width / 2, 0.5), hh = Math.Max(e.Height / 2, 0.5);
        if (Math.Abs(d.X) < 1e-9 && Math.Abs(d.Y) < 1e-9) return e.Center;

        double t;
        if (e.Kind == WbKind.Ellipse)
        {
            t = 1 / Math.Sqrt(d.X * d.X / (hw * hw) + d.Y * d.Y / (hh * hh));
        }
        else
        {
            var tx = Math.Abs(d.X) < 1e-9 ? double.MaxValue : hw / Math.Abs(d.X);
            var ty = Math.Abs(d.Y) < 1e-9 ? double.MaxValue : hh / Math.Abs(d.Y);
            t = Math.Min(tx, ty);
        }
        return LocalToWorld(e, new WbPoint(d.X * t, d.Y * t));
    }

    /// <summary>
    /// Resizes by dragging a handle. (hx, hy) identify the handle: -1 = left/top, 0 = middle, 1 = right/bottom.
    /// The opposite side stays where it is, also when the element is rotated.
    /// </summary>
    public static void Resize(WbElement e, int hx, int hy, WbPoint mouseWorld, bool keepAspect)
    {
        var anchorLocal = new WbPoint(-hx * e.Width / 2, -hy * e.Height / 2);
        var anchorWorld = LocalToWorld(e, anchorLocal);
        var d = Rotate(mouseWorld - anchorWorld, -e.Rotation);

        var newW = hx != 0 ? Math.Max(MinSize, d.X * hx) : e.Width;
        var newH = hy != 0 ? Math.Max(MinSize, d.Y * hy) : e.Height;

        if (keepAspect && hx != 0 && hy != 0 && e.Width > 0 && e.Height > 0)
        {
            var scale = Math.Max(newW / e.Width, newH / e.Height);
            newW = e.Width * scale;
            newH = e.Height * scale;
        }

        var centerWorld = anchorWorld + Rotate(new WbPoint(hx * newW / 2, hy * newH / 2), e.Rotation);
        e.Width = newW;
        e.Height = newH;
        e.X = centerWorld.X - newW / 2;
        e.Y = centerWorld.Y - newH / 2;
    }

    // ------------------------------------------------------------------ points

    /// <summary>Removes points that do not change the shape (Ramer–Douglas–Peucker).</summary>
    public static List<WbPoint> Simplify(IReadOnlyList<WbPoint> points, double tolerance)
    {
        if (points.Count < 3) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double max = 0;
            var index = -1;
            for (var i = first + 1; i < last; i++)
            {
                var distance = DistanceToSegment(points[i], points[first], points[last]);
                if (distance > max) { max = distance; index = i; }
            }
            if (index < 0 || max <= tolerance) continue;
            keep[index] = true;
            stack.Push((first, index));
            stack.Push((index, last));
        }
        var result = new List<WbPoint>();
        for (var i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
        return result;
    }

    public static double DistanceToSegment(WbPoint p, WbPoint a, WbPoint b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared < 1e-12) return (p - a).Length;
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared, 0, 1);
        return (p - new WbPoint(a.X + ab.X * t, a.Y + ab.Y * t)).Length;
    }

    /// <summary>
    /// Turns world points into an element: the box is their bounding box and the points
    /// are stored normalized (0..1) inside it, so resizing the box scales the drawing.
    /// </summary>
    public static WbElement FromPoints(WbKind kind, IReadOnlyList<WbPoint> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        double right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        // A perfectly horizontal / vertical line still needs a box with some thickness.
        if (right - left < 1) { var c = (left + right) / 2; left = c - 0.5; right = c + 0.5; }
        if (bottom - top < 1) { var c = (top + bottom) / 2; top = c - 0.5; bottom = c + 0.5; }
        double w = right - left, h = bottom - top;

        return new WbElement
        {
            Kind = kind,
            X = left,
            Y = top,
            Width = w,
            Height = h,
            Points = points.Select(p => new WbPoint(Math.Round((p.X - left) / w, 4), Math.Round((p.Y - top) / h, 4))).ToList()
        };
    }

    // ------------------------------------------------------------------ document operations

    public static WbElement? Find(WhiteboardData data, string? id) =>
        id is null ? null : data.Elements.FirstOrDefault(e => e.Id == id);

    public static bool HasConnector(WhiteboardData data, string a, string b) =>
        data.Connectors.Any(c => c.Links(a, b));

    /// <summary>Links two elements. Returns null when they are the same element or already linked.</summary>
    public static WbConnector? AddConnector(WhiteboardData data, string fromId, string toId, double thickness = 1, bool arrow = false)
    {
        if (fromId == toId || Find(data, fromId) is null || Find(data, toId) is null) return null;
        if (HasConnector(data, fromId, toId)) return null;
        var connector = new WbConnector { FromId = fromId, ToId = toId, Thickness = thickness, Arrow = arrow };
        data.Connectors.Add(connector);
        return connector;
    }

    /// <summary>Deletes elements together with the connectors attached to them.</summary>
    public static void Delete(WhiteboardData data, ICollection<string> ids)
    {
        data.Elements.RemoveAll(e => ids.Contains(e.Id));
        data.Connectors.RemoveAll(c => ids.Contains(c.FromId) || ids.Contains(c.ToId));
    }

    /// <summary>Removes every connector attached to the given elements. Returns how many were removed.</summary>
    public static int Disconnect(WhiteboardData data, ICollection<string> ids) =>
        data.Connectors.RemoveAll(c => ids.Contains(c.FromId) || ids.Contains(c.ToId));

    /// <summary>
    /// Merges the given elements into one group element that moves, rotates and scales as a whole.
    /// Connectors of the members are moved to the group (without creating duplicates).
    /// </summary>
    public static WbElement? Group(WhiteboardData data, ICollection<string> ids)
    {
        var members = data.Elements.Where(e => ids.Contains(e.Id)).ToList();
        if (members.Count < 2) return null;

        var bounds = Bounds(members);
        double w = Math.Max(1, bounds.Width), h = Math.Max(1, bounds.Height);
        var group = new WbElement
        {
            Kind = WbKind.Group,
            X = bounds.Left,
            Y = bounds.Top,
            Width = w,
            Height = h,
            RefWidth = w,
            RefHeight = h,
            StrokeWidth = 0,
            Children = members
        };
        foreach (var member in members)
        {
            member.X -= bounds.Left;
            member.Y -= bounds.Top;
        }

        var index = data.Elements.IndexOf(members[^1]);
        data.Elements.Insert(index + 1, group);
        data.Elements.RemoveAll(members.Contains);

        var memberIds = members.Select(m => m.Id).ToHashSet();
        var kept = new List<WbConnector>();
        foreach (var connector in data.Connectors)
        {
            if (memberIds.Contains(connector.FromId)) connector.FromId = group.Id;
            if (memberIds.Contains(connector.ToId)) connector.ToId = group.Id;
            if (connector.FromId == connector.ToId) continue;                       // was inside the group
            if (kept.Any(k => k.Links(connector.FromId, connector.ToId))) continue; // would be a duplicate
            kept.Add(connector);
        }
        data.Connectors = kept;

        // The star and the references of the members now belong to the whole. The members keep their own
        // (nothing reads them while they are merged) so that a later split gives everything back to its owner.
        group.Favorite = members.Any(m => m.Favorite);
        var cards = members.SelectMany(m => m.CardRefs ?? new List<string>()).Distinct().ToList();
        var docs = members.SelectMany(m => m.DocRefs ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        group.CardRefs = cards.Count > 0 ? cards : null;
        group.DocRefs = docs.Count > 0 ? docs : null;
        return group;
    }

    /// <summary>Splits a group back into its elements. Returns them (empty when the id is not a group).</summary>
    public static List<WbElement> Ungroup(WhiteboardData data, string groupId)
    {
        var group = Find(data, groupId);
        if (group?.Kind != WbKind.Group || group.Children is null) return new List<WbElement>();

        var sx = group.RefWidth > 0 ? group.Width / group.RefWidth : 1;
        var sy = group.RefHeight > 0 ? group.Height / group.RefHeight : 1;
        var children = group.Children;

        foreach (var child in children)
        {
            // Center of the child inside the (scaled) group, relative to the group center.
            var local = new WbPoint((child.X + child.Width / 2) * sx - group.Width / 2,
                                    (child.Y + child.Height / 2) * sy - group.Height / 2);
            var center = LocalToWorld(group, local);
            child.Width *= sx;
            child.Height *= sy;
            child.X = center.X - child.Width / 2;
            child.Y = center.Y - child.Height / 2;
            child.Rotation = NormalizeAngle(child.Rotation + group.Rotation);
            var average = Math.Sqrt(Math.Abs(sx * sy));
            child.StrokeWidth = Math.Round(child.StrokeWidth * average, 2);
            if (child.FontSize > 0) child.FontSize = Math.Round(child.FontSize * sy, 1);
        }

        // Nothing is lost when the group disappears. Every element gets back what was its own and is still
        // on the group (a reference removed while merged stays removed); what was added to the whole, and the
        // star when no element had one, go to the first element.
        if (children.Count > 0)
        {
            var groupCards = group.CardRefs ?? new List<string>();
            var groupDocs = group.DocRefs ?? new List<string>();
            foreach (var child in children)
            {
                child.Favorite &= group.Favorite;
                child.CardRefs = Kept(child.CardRefs?.Where(groupCards.Contains));
                child.DocRefs = Kept(child.DocRefs?.Where(d => groupDocs.Contains(d, StringComparer.OrdinalIgnoreCase)));
            }

            var heir = children[0];
            if (group.Favorite && !children.Any(c => c.Favorite)) heir.Favorite = true;
            var ownedCards = children.SelectMany(c => c.CardRefs ?? new List<string>()).ToHashSet();
            var ownedDocs = children.SelectMany(c => c.DocRefs ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // (a task has one element only: the first owner keeps it)
            foreach (var child in children.Skip(1).Where(c => c.CardRefs != null))
                child.CardRefs = Kept(child.CardRefs!.Where(id => !children.TakeWhile(o => o != child).Any(o => o.CardRefs?.Contains(id) == true)));
            heir.CardRefs = Kept((heir.CardRefs ?? new List<string>()).Concat(groupCards.Where(id => !ownedCards.Contains(id))));
            heir.DocRefs = Kept((heir.DocRefs ?? new List<string>()).Concat(groupDocs.Where(d => !ownedDocs.Contains(d))));
        }

        var index = data.Elements.IndexOf(group);
        data.Elements.RemoveAt(index);
        data.Elements.InsertRange(index, children);
        data.Connectors.RemoveAll(c => c.FromId == groupId || c.ToId == groupId);
        return children;
    }

    private static List<string>? Kept(IEnumerable<string>? references)
    {
        var list = references?.Distinct().ToList();
        return list is { Count: > 0 } ? list : null;
    }

    /// <summary>Copies elements (with new ids) and the connectors between them.</summary>
    public static List<WbElement> Duplicate(WhiteboardData data, ICollection<string> ids, double offset = 24)
    {
        var map = new Dictionary<string, string>();
        var copies = new List<WbElement>();
        foreach (var original in data.Elements.Where(e => ids.Contains(e.Id)).ToList())
        {
            var copy = CloneWithNewIds(original);
            copy.X += offset;
            copy.Y += offset;
            map[original.Id] = copy.Id;
            copies.Add(copy);
        }
        data.Elements.AddRange(copies);

        foreach (var connector in data.Connectors.ToList())
        {
            if (map.TryGetValue(connector.FromId, out var from) && map.TryGetValue(connector.ToId, out var to))
                data.Connectors.Add(new WbConnector
                {
                    FromId = from, ToId = to, Thickness = connector.Thickness, Color = connector.Color, Arrow = connector.Arrow
                });
        }
        return copies;
    }

    public static WbElement CloneWithNewIds(WbElement element)
    {
        var copy = JsonFile.Clone(element);
        AssignNewIds(copy);
        return copy;
    }

    private static void AssignNewIds(WbElement element)
    {
        element.Id = Guid.NewGuid().ToString("N")[..12];
        // A task is linked to one element only: a copy never takes the links of the original.
        element.CardRefs = null;
        if (element.Children != null)
            foreach (var child in element.Children) AssignNewIds(child);
    }

    public static void BringToFront(WhiteboardData data, ICollection<string> ids)
    {
        var moved = data.Elements.Where(e => ids.Contains(e.Id)).ToList();
        data.Elements.RemoveAll(moved.Contains);
        data.Elements.AddRange(moved);
    }

    public static void SendToBack(WhiteboardData data, ICollection<string> ids)
    {
        var moved = data.Elements.Where(e => ids.Contains(e.Id)).ToList();
        data.Elements.RemoveAll(moved.Contains);
        data.Elements.InsertRange(0, moved);
    }

    /// <summary>Every image file referenced by the board (also inside groups).</summary>
    public static IEnumerable<string> ImageFiles(IEnumerable<WbElement> elements)
    {
        foreach (var element in elements)
        {
            if (!string.IsNullOrEmpty(element.Image)) yield return element.Image;
            if (element.Children != null)
                foreach (var file in ImageFiles(element.Children)) yield return file;
        }
    }

    public static double NormalizeAngle(double degrees)
    {
        degrees %= 360;
        if (degrees < 0) degrees += 360;
        return Math.Round(degrees, 2);
    }
}
