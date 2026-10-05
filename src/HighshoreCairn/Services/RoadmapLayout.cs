using System.Globalization;
using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>How a piece of a roadmap bar is painted.</summary>
public enum BarFill
{
    /// <summary>Full color: the period of the work.</summary>
    Dark,
    /// <summary>Soft color: time left before the due date (or saved by finishing early).</summary>
    Light,
    /// <summary>Alternating dark and light stripes: a delay.</summary>
    Striped,
    /// <summary>Soft color with a dark border: a plain start-to-finish bar.</summary>
    Outline,
    /// <summary>Two milestones overlap: stripes of the two colors (or color / transparent when they are the same).</summary>
    Mixed
}

public class RoadmapSegment
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public BarFill Fill { get; set; }
    /// <summary>Mixed only: the color of the other milestone.</summary>
    public string? Color2 { get; set; }
}

public enum RoadmapBarKind { Card, Custom, Milestone }

/// <summary>A task or a milestone as drawn in the roadmap.</summary>
public class RoadmapBar
{
    public string Id { get; set; } = "";
    public RoadmapBarKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Color { get; set; } = "#1F6F8B";
    /// <summary>Milestones: the icon of the type ("ion:name" or "file:name").</summary>
    public string? Icon { get; set; }
    public List<RoadmapSegment> Segments { get; } = new();
    public DateTime Start => Segments.Count == 0 ? default : Segments.Min(s => s.From);
    public DateTime End => Segments.Count == 0 ? default : Segments.Max(s => s.To);
    /// <summary>Where the title starts (after the part shared with the previous milestone).</summary>
    public DateTime LabelFrom { get; set; }
}

public enum RoadmapRowKind { Milestones, Custom, Tag, Untagged }

public class RoadmapRow
{
    /// <summary>"milestones", "custom", "untagged" or the id of the tag.</summary>
    public string Key { get; set; } = "";
    public RoadmapRowKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Color { get; set; } = "#5F6B7A";
    public bool Collapsed { get; set; }
    /// <summary>Bars that overlap in time go to different lanes (lines) of the row.</summary>
    public List<List<RoadmapBar>> Lanes { get; } = new();
    public int Count => Lanes.Sum(l => l.Count);
}

/// <summary>The rules of the milestones that share a level (a line of the roadmap).</summary>
public static class MilestoneRules
{
    /// <summary>The level (1-5) of a milestone: the one of its type, 1 when the type no longer exists.</summary>
    public static int LevelOf(Milestone milestone, RoadmapSettings settings) =>
        Math.Clamp(settings.MilestoneTypes.FirstOrDefault(t => t.Id == milestone.TypeId)?.Level ?? 1, 1, 5);

    public static bool SameDates(Milestone a, Milestone b) => a.Start.Date == b.Start.Date && a.End.Date == b.End.Date;

    /// <summary>Two periods share at least one day.</summary>
    public static bool Overlap(Milestone a, Milestone b) => a.Start.Date <= b.End.Date && b.Start.Date <= a.End.Date;

    /// <summary>
    /// The milestones of the list (all of one level) that would be completely hidden: each of them lies
    /// inside another one, or inside two overlapping ones (one that starts before it, one that ends after it).
    /// A milestone that lies inside it cannot be one of those that hide it: of two that share a start or an
    /// end day, the shorter one is the hidden one. Of two milestones with exactly the same dates, the second
    /// one counts as hidden.
    /// </summary>
    public static List<Milestone> Covered(IReadOnlyList<Milestone> level)
    {
        // Exact duplicates first: only the first milestone of each period takes part in the rest.
        var unique = new List<Milestone>();
        var result = new List<Milestone>();
        foreach (var milestone in level)
        {
            if (unique.Any(u => SameDates(u, milestone))) result.Add(milestone);
            else unique.Add(milestone);
        }

        foreach (var b in unique)
        {
            // Those that can hide b: every other milestone that is not itself inside b.
            var around = unique.Where(x => !ReferenceEquals(x, b) && !(x.Start.Date >= b.Start.Date && x.End.Date <= b.End.Date)).ToList();
            var covered = around.Where(a => a.Start.Date <= b.Start.Date)
                .Any(a => around.Any(c => c.End.Date >= b.End.Date && Overlap(a, c)));
            if (covered) result.Add(b);
        }
        return result;
    }

    /// <summary>All the milestones that are hidden by others of their level.</summary>
    public static List<Milestone> Hidden(IEnumerable<Milestone> all, RoadmapSettings settings) =>
        all.GroupBy(m => LevelOf(m, settings))
            .SelectMany(level => Covered(level.OrderBy(m => m.Start.Date).ThenBy(m => m.End.Date).ToList())).ToList();

    /// <summary>
    /// What saving <paramref name="edited"/> would do to its level: an identical milestone already there
    /// (not allowed), or the milestones that would end up hidden (possibly the edited one itself).
    /// </summary>
    public static (Milestone? Identical, List<Milestone> Hidden) Check(
        Milestone edited, IEnumerable<Milestone> all, RoadmapSettings settings)
    {
        var level = LevelOf(edited, settings);
        var others = all.Where(m => m.Id != edited.Id && LevelOf(m, settings) == level).ToList();
        var identical = others.FirstOrDefault(m => SameDates(m, edited));
        if (identical != null) return (identical, new List<Milestone>());

        // What is already hidden today is not the fault of this change.
        var before = Covered(others).Select(m => m.Id).ToHashSet();
        var after = Covered(others.Append(edited).ToList());
        return (null, after.Where(m => !before.Contains(m.Id)).ToList());
    }
}

/// <summary>The window of time shown by the roadmap: whole months of one calendar year.</summary>
public class RoadmapTimeline
{
    public int Year { get; private set; }
    /// <summary>1-12: the month on the left.</summary>
    public int StartMonth { get; private set; } = 1;
    /// <summary>1-12.</summary>
    public int Months { get; private set; } = 6;

    public RoadmapTimeline(DateTime today, int months = 6)
    {
        Months = Math.Clamp(months, 1, 12);
        Today(today);
    }

    // Kept away from the ends of what a date can hold, so that the arithmetic around the view never overflows.
    private const int MinYear = 2, MaxYear = 9990;

    public DateTime From => new(Year, StartMonth, 1);
    public DateTime To => From.AddMonths(Months);

    public void SetMonths(int months)
    {
        Months = Math.Clamp(months, 1, 12);
        if (StartMonth + Months - 1 > 12) StartMonth = 13 - Months;
    }

    /// <summary>One month later; from December the view jumps to the next year, January on the left.</summary>
    public void Next()
    {
        if (StartMonth + Months - 1 >= 12)
        {
            if (Year >= MaxYear) return;
            Year++;
            StartMonth = 1;
        }
        else StartMonth++;
    }

    /// <summary>One month earlier; from January the view jumps to the previous year, December on the right.</summary>
    public void Previous()
    {
        if (StartMonth <= 1)
        {
            if (Year <= MinYear) return;
            Year--;
            StartMonth = 13 - Months;
        }
        else StartMonth--;
    }

    /// <summary>Back to the current month, as far to the left as the year allows.</summary>
    public void Today(DateTime today)
    {
        Year = Math.Clamp(today.Year, MinYear, MaxYear);
        StartMonth = Math.Min(today.Month, 13 - Months);
    }

    /// <summary>"May 2026" for one month, "Jan – Jun 2026" for a range.</summary>
    public string Label
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            if (Months == 1) return From.ToString("MMMM yyyy", inv);
            return $"{From.ToString("MMM", inv)} – {To.AddDays(-1).ToString("MMM", inv)} {Year}";
        }
    }
}

/// <summary>Turns the board and the roadmap data into rows of bars. No UI here: everything is testable.</summary>
public static class RoadmapLayout
{
    public const string MilestonesKey = "milestones";
    public const string CustomKey = "custom";
    public const string UntaggedKey = "untagged";

    /// <summary>The limits of the free view: from one week to two years on screen.</summary>
    public const double MinViewDays = 7;
    public const double MaxViewDays = 730;

    /// <summary>"02 May 2026 - 18w" (ISO week), or just the date.</summary>
    public static string FormatHover(DateTime date, bool withWeek)
    {
        var text = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        return withWeek ? $"{text} - {ISOWeek.GetWeekOfYear(date)}w" : text;
    }

    private static DateTime EndOf(DateTime value, bool hasTime) => hasTime ? value : value.Date.AddDays(1);

    private static void Add(RoadmapBar bar, DateTime from, DateTime to, BarFill fill)
    {
        if (to <= from) return;
        bar.Segments.Add(new RoadmapSegment { From = from, To = to, Fill = fill });
    }

    /// <summary>
    /// The pieces of a task bar.
    ///   finish        -> one plain bar from start to finish (due and end are ignored)
    ///   end, no due   -> dark from start to end (plain: nothing to compare with)
    ///   start &lt;= due  -> dark while working; then light up to the due date (finished early) or striped past it (late)
    ///   start &gt; due   -> striped from the due date to the start (started late), then dark
    /// A task that is still open uses "now" as its end.
    /// </summary>
    private static void Fill(RoadmapBar bar, DateTime start, DateTime? due, DateTime? end, DateTime? finish, bool endActsAsFinish)
    {
        var minimum = start.AddDays(1);
        if (finish is { } f)
        {
            Add(bar, start, f < minimum ? minimum : f, BarFill.Outline);
            return;
        }
        if (end is not { } e) return;
        if (e < minimum) e = minimum;

        if (due is not { } d)
        {
            Add(bar, start, e, endActsAsFinish ? BarFill.Outline : BarFill.Dark);
            return;
        }

        if (start >= d)
        {
            Add(bar, d, start, BarFill.Striped);
            Add(bar, start, e, BarFill.Dark);
        }
        else if (e <= d)
        {
            Add(bar, start, e, BarFill.Dark);
            Add(bar, e, d, BarFill.Light);
        }
        else
        {
            Add(bar, start, d, BarFill.Dark);
            Add(bar, d, e, BarFill.Striped);
        }
    }

    /// <summary>The bar of a card, or null when the card has not the dates the roadmap needs.</summary>
    public static RoadmapBar? CardBar(CardData card, bool inDoneColumn, RoadmapSettings settings, DateTime now)
    {
        if (card.StartDate is null) return null;
        var start = card.StartDate.Value.Date;
        DateTime? due = card.DueDate is null ? null : EndOf(card.DueDate.Value, card.DueHasTime);
        DateTime? end = card.EndDate is null ? null : EndOf(card.EndDate.Value, card.EndHasTime);
        DateTime? finish = card.FinishDate is null ? null : card.FinishDate.Value.Date.AddDays(1);

        var bar = new RoadmapBar { Id = card.Id, Kind = RoadmapBarKind.Card, Title = card.Title };
        if (finish is null && end is null)
        {
            // Not closed yet: shown only when the option is on and there is a due date to compare with.
            if (!settings.ShowInProgress || due is null) return null;
            if (inDoneColumn) end = due;                     // done, but nobody wrote when: assume on time
            else if (now <= start)
            {
                Add(bar, start, due.Value > start ? due.Value : start.AddDays(1), BarFill.Light);   // not started yet
                bar.LabelFrom = bar.Start;
                return bar;
            }
            else end = now;
        }
        Fill(bar, start, due, end, finish, endActsAsFinish: false);
        if (bar.Segments.Count == 0) return null;
        bar.LabelFrom = bar.Start;
        return bar;
    }

    /// <summary>The bar of a task of the Custom row (without a due date, the end date gives a plain bar).</summary>
    public static RoadmapBar? ItemBar(RoadmapItem item, DateTime now)
    {
        var start = item.Start.Date;
        DateTime? due = item.Due?.Date.AddDays(1);
        DateTime? end = item.End?.Date.AddDays(1);
        var bar = new RoadmapBar { Id = item.Id, Kind = RoadmapBarKind.Custom, Title = item.Name };
        if (end is null)
        {
            if (due is null) return null;
            if (now <= start)
            {
                Add(bar, start, due.Value > start ? due.Value : start.AddDays(1), BarFill.Light);
                bar.LabelFrom = bar.Start;
                return bar;
            }
            end = now;
        }
        Fill(bar, start, due, end, null, endActsAsFinish: true);
        if (bar.Segments.Count == 0) return null;
        bar.LabelFrom = bar.Start;
        return bar;
    }

    /// <summary>Greedy packing: every bar goes to the first lane where it does not overlap another one.</summary>
    public static List<List<RoadmapBar>> Lanes(IEnumerable<RoadmapBar> bars)
    {
        var lanes = new List<List<RoadmapBar>>();
        foreach (var bar in bars.OrderBy(b => b.Start).ThenBy(b => b.End).ThenBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var lane = lanes.FirstOrDefault(l => l[^1].End <= bar.Start);
            if (lane is null) lanes.Add(lane = new List<RoadmapBar>());
            lane.Add(bar);
        }
        return lanes;
    }

    /// <summary>
    /// The lines of the Milestones row: one per level in use (lowest first). Milestones of a level stay on
    /// the same line: the hidden ones are left out, the shared days of two neighbours are painted "mixed".
    /// </summary>
    public static List<List<RoadmapBar>> MilestoneLanes(RoadmapData data, RoadmapSettings settings)
    {
        var lanes = new List<List<RoadmapBar>>();
        foreach (var level in data.Milestones.GroupBy(m => MilestoneRules.LevelOf(m, settings)).OrderBy(g => g.Key))
        {
            var all = level.OrderBy(m => m.Start.Date).ThenBy(m => m.End.Date).ToList();
            var hidden = MilestoneRules.Covered(all).Select(m => m.Id).ToHashSet();
            var visible = all.Where(m => !hidden.Contains(m.Id)).ToList();
            var lane = new List<RoadmapBar>();
            string? previousColor = null;
            DateTime previousEnd = default;

            foreach (var milestone in visible)
            {
                var type = settings.MilestoneTypes.FirstOrDefault(t => t.Id == milestone.TypeId);
                var bar = new RoadmapBar
                {
                    Id = milestone.Id,
                    Kind = RoadmapBarKind.Milestone,
                    Title = milestone.Name,
                    Color = type?.Color ?? settings.MilestonesColor,
                    Icon = type?.Icon ?? MilestoneType.DefaultIcon
                };
                var from = milestone.Start.Date;
                var to = milestone.End.Date.AddDays(1);
                if (to <= from) to = from.AddDays(1);

                if (previousColor != null && from < previousEnd)
                {
                    // The days shared with the previous milestone belong to both.
                    var shared = previousEnd < to ? previousEnd : to;
                    bar.Segments.Add(new RoadmapSegment { From = from, To = shared, Fill = BarFill.Mixed, Color2 = previousColor });
                    Add(bar, shared, to, BarFill.Dark);
                    bar.LabelFrom = shared < to ? shared : from;
                    // ...so the previous bar ends where this one starts.
                    var previous = lane[^1];
                    var last = previous.Segments[^1];
                    if (last.From < from) last.To = from;
                    else
                    {
                        // Shared on both sides: nothing of its own is left, the name goes over the shared days.
                        previous.Segments.Remove(last);
                        previous.LabelFrom = previous.Segments.Count > 0 ? previous.Segments[0].From : previous.LabelFrom;
                    }
                }
                else
                {
                    Add(bar, from, to, BarFill.Dark);
                    bar.LabelFrom = from;
                }

                lane.Add(bar);
                previousColor = bar.Color;
                previousEnd = to;
            }
            if (lane.Count > 0) lanes.Add(lane);
        }
        return lanes;
    }

    /// <summary>The tags in the order of their rows: the saved order first, then the others in board order.</summary>
    public static List<TagDef> OrderedTags(BoardData board, RoadmapData data)
    {
        var result = new List<TagDef>();
        foreach (var id in data.TagOrder)
            if (board.Tags.FirstOrDefault(t => t.Id == id) is { } tag && !result.Contains(tag)) result.Add(tag);
        foreach (var tag in board.Tags)
            if (!result.Contains(tag)) result.Add(tag);
        return result;
    }

    /// <summary>All the rows of the roadmap, top to bottom.</summary>
    public static List<RoadmapRow> Build(BoardData board, RoadmapData data, RoadmapSettings settings, DateTime now)
    {
        var rows = new List<RoadmapRow>();

        if (settings.ShowMilestones)
        {
            var row = new RoadmapRow
            {
                Key = MilestonesKey, Kind = RoadmapRowKind.Milestones,
                Title = settings.MilestonesName, Color = settings.MilestonesColor
            };
            row.Lanes.AddRange(MilestoneLanes(data, settings));
            rows.Add(row);
        }

        if (settings.ShowCustom)
        {
            var row = new RoadmapRow
            {
                Key = CustomKey, Kind = RoadmapRowKind.Custom,
                Title = settings.CustomName, Color = settings.CustomColor
            };
            var bars = data.Items.Select(i => ItemBar(i, now)).Where(b => b != null).Select(b => b!).ToList();
            foreach (var bar in bars) bar.Color = settings.CustomColor;
            row.Lanes.AddRange(Lanes(bars));
            rows.Add(row);
        }

        var cards = board.Columns.SelectMany(c => c.Cards.Select(card => (Card: card, Done: c.IsDone))).ToList();
        foreach (var tag in OrderedTags(board, data))
        {
            var row = new RoadmapRow { Key = tag.Id, Kind = RoadmapRowKind.Tag, Title = tag.Name, Color = tag.Color };
            var bars = new List<RoadmapBar>();
            foreach (var (card, done) in cards.Where(c => c.Card.TagIds.Contains(tag.Id)))
            {
                if (CardBar(card, done, settings, now) is not { } bar) continue;
                bar.Color = tag.Color;
                bars.Add(bar);
            }
            row.Lanes.AddRange(Lanes(bars));
            rows.Add(row);
        }

        // Tasks without a (still existing) tag would otherwise never appear.
        var tagIds = board.Tags.Select(t => t.Id).ToHashSet();
        var untagged = new List<RoadmapBar>();
        foreach (var (card, done) in cards.Where(c => !c.Card.TagIds.Any(tagIds.Contains)))
        {
            if (CardBar(card, done, settings, now) is not { } bar) continue;
            bar.Color = "#8D8D8D";
            untagged.Add(bar);
        }
        if (untagged.Count > 0)
        {
            var row = new RoadmapRow { Key = UntaggedKey, Kind = RoadmapRowKind.Untagged, Title = "No tag", Color = "#8D8D8D" };
            row.Lanes.AddRange(Lanes(untagged));
            rows.Add(row);
        }

        foreach (var row in rows) row.Collapsed = data.Collapsed.Contains(row.Key);
        return rows;
    }

    /// <summary>Moves a tag row up or down and stores the resulting order.</summary>
    public static bool MoveTag(BoardData board, RoadmapData data, string tagId, int delta)
    {
        var order = OrderedTags(board, data).Select(t => t.Id).ToList();
        var index = order.IndexOf(tagId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count) return false;
        order.RemoveAt(index);
        order.Insert(target, tagId);
        data.TagOrder = order;
        return true;
    }

    /// <summary>Puts a tag row at a given position (drag and drop of the row headers).</summary>
    public static bool PlaceTag(BoardData board, RoadmapData data, string tagId, int position)
    {
        var order = OrderedTags(board, data).Select(t => t.Id).ToList();
        var index = order.IndexOf(tagId);
        if (index < 0) return false;
        order.RemoveAt(index);
        position = Math.Clamp(position, 0, order.Count);
        order.Insert(position, tagId);
        var changed = !order.SequenceEqual(OrderedTags(board, data).Select(t => t.Id));
        data.TagOrder = order;
        return changed;
    }
}

/// <summary>Reads and writes roadmap.json and the custom icon pictures of a project.</summary>
public class RoadmapService
{
    public const string FileName = "roadmap.json";
    public const string IconsFolderName = "roadmap-icons";

    private readonly string _projectFolder;

    public RoadmapService(string projectFolder) => _projectFolder = projectFolder;

    public string File => Path.Combine(_projectFolder, FileName);
    public string IconsFolder => Path.Combine(_projectFolder, IconsFolderName);

    public RoadmapData Load()
    {
        var data = JsonFile.Load<RoadmapData>(File) ?? new RoadmapData();
        data.Months = Math.Clamp(data.Months, 1, 12);
        return data;
    }

    /// <summary>
    /// Renames a roadmap file that cannot be read, so that the next save does not replace it with an
    /// empty one. Returns the name of the copy, or null when there was nothing to keep.
    /// </summary>
    public string? SetAsideUnreadable(DateTime now)
    {
        if (!System.IO.File.Exists(File)) return null;
        var name = FileName + ".unreadable-" + now.ToString("yyyyMMddHHmmss");
        System.IO.File.Move(File, Path.Combine(_projectFolder, name), true);
        return name;
    }

    public void Save(RoadmapData data) => JsonFile.Save(File, data);

    public string IconPath(string fileName) => Path.Combine(IconsFolder, Path.GetFileName(fileName));

    /// <summary>Copies an external picture (or SVG) into the project and returns its "file:name" reference.</summary>
    public string ImportIcon(string sourceFile)
    {
        Directory.CreateDirectory(IconsFolder);
        var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        var stem = new string(Path.GetFileNameWithoutExtension(sourceFile)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "icon";
        if (stem.Length > 40) stem = stem[..40];
        var name = stem + extension;
        var n = 2;
        while (System.IO.File.Exists(IconPath(name))) name = $"{stem}-{n++}{extension}";
        System.IO.File.Copy(sourceFile, IconPath(name));
        return IconLibrary.FilePrefix + name;
    }

    /// <summary>The picture files referenced by the milestone types.</summary>
    public static IEnumerable<string> IconFiles(RoadmapSettings settings) =>
        settings.MilestoneTypes.Select(t => IconLibrary.FileName(t.Icon)).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the icon pictures used by the settings from one folder to another (missing files are skipped).</summary>
    public static void CopyIcons(RoadmapSettings settings, string fromFolder, string toFolder)
    {
        foreach (var name in IconFiles(settings))
        {
            var source = Path.Combine(fromFolder, Path.GetFileName(name));
            if (!System.IO.File.Exists(source)) continue;
            Directory.CreateDirectory(toFolder);
            System.IO.File.Copy(source, Path.Combine(toFolder, Path.GetFileName(name)), overwrite: true);
        }
    }
}
