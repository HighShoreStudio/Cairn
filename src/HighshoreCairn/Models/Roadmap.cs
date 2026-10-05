using System.Text.Json.Serialization;

namespace HighshoreCairn.Models;

/// <summary>A kind of milestone ("Major Release", "Bug fix", ...): icon, color and the row it is drawn on.</summary>
public class MilestoneType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";

    /// <summary>
    /// "ion:rocket-outline" = an icon of the built-in set (Ionicons);
    /// "file:name.png" = a picture stored in the roadmap-icons folder of the project.
    /// </summary>
    public string Icon { get; set; } = DefaultIcon;
    public string Color { get; set; } = "#5B5BD6";

    /// <summary>1-5: milestones of the same level share a row; lower levels are drawn above.</summary>
    public int Level { get; set; } = 1;

    public const string DefaultIcon = "ion:flag-outline";

    public static List<MilestoneType> Defaults() => new()
    {
        new MilestoneType { Id = "major", Name = "Major Release", Icon = "ion:rocket", Color = "#E5484D" },
        new MilestoneType { Id = "minor", Name = "Minor Release", Icon = "ion:cube-outline", Color = "#F2A541" },
        new MilestoneType { Id = "bugfix", Name = "Bug fix", Icon = "ion:bug-outline", Color = "#30A46C" },
        new MilestoneType { Id = "beta", Name = "Internal/Beta", Icon = "ion:flask-outline", Color = "#5B5BD6" }
    };
}

/// <summary>Customization of the roadmap of a project (stored in project.json, copied by the default template).</summary>
public class RoadmapSettings
{
    public bool ShowMilestones { get; set; } = true;
    public string MilestonesName { get; set; } = "Milestones";
    public string MilestonesColor { get; set; } = "#16324F";

    public bool ShowCustom { get; set; } = true;
    public string CustomName { get; set; } = "Custom";
    public string CustomColor { get; set; } = "#5F6B7A";

    /// <summary>The date under the mouse also shows the week of the year ("02 May 2026 - 18w").</summary>
    public bool ShowWeekNumber { get; set; } = true;

    /// <summary>Tasks with a start and a due date but not finished yet are drawn up to today.</summary>
    public bool ShowInProgress { get; set; } = true;

    /// <summary>A card that enters a "done" column and has no end date gets the current date.</summary>
    public bool AutoEndDate { get; set; } = true;

    // Colors of the selected bar (a neutral highlight that keeps the title readable), per theme.
    public string SelectionBackLight { get; set; } = "#16324F";
    public string SelectionTextLight { get; set; } = "#FFFFFF";
    public string SelectionBackDark { get; set; } = "#F4F6F8";
    public string SelectionTextDark { get; set; } = "#0F1720";

    public List<MilestoneType> MilestoneTypes { get; set; } = MilestoneType.Defaults();
}

/// <summary>A release (or any other milestone): the period from the start of the work to the release day.</summary>
public class Milestone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    /// <summary>First day (the work starts).</summary>
    public DateTime Start { get; set; }
    /// <summary>Last day (the release).</summary>
    public DateTime End { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A task of the "Custom" row: it only exists in the roadmap.</summary>
public class RoadmapItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    /// <summary>Optional. Without it the end date behaves like the "finish date" of a card.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Due { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? End { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Content of roadmap.json: what exists only in the roadmap, plus the state of its rows.</summary>
public class RoadmapData
{
    public int Version { get; set; } = 1;
    public List<Milestone> Milestones { get; set; } = new();
    public List<RoadmapItem> Items { get; set; } = new();
    /// <summary>Ids of the tags in the order of their rows (tags not listed come after, in board order).</summary>
    public List<string> TagOrder { get; set; } = new();
    /// <summary>Keys of the rows closed with the eye button.</summary>
    public List<string> Collapsed { get; set; } = new();
    /// <summary>How many months the timeline shows (1-12).</summary>
    public int Months { get; set; } = 6;
}
