namespace HighshoreCairn.Models;

public enum Priority { None, Low, Medium, High, Urgent }

public enum DueFilter { Any, Overdue, ThisWeek, ThisMonth, NoDueDate }

public enum SortMode { Manual, Priority, Created, DueDate, Assignee }

/// <summary>Content of kanban.json: the whole board state.</summary>
public class BoardData
{
    /// <summary>2 = columns have separate colors for the light and the dark theme.</summary>
    public int Version { get; set; } = 2;
    public List<TagDef> Tags { get; set; } = new();
    public List<ColumnData> Columns { get; set; } = new();
    public List<CardData> Archived { get; set; } = new();
    public List<SavedFilter> SavedFilters { get; set; } = new();

    public static BoardData CreateDefault() => new()
    {
        Columns =
        {
            new ColumnData { Name = "TODO" },
            new ColumnData { Name = "In Progress" },
            new ColumnData { Name = "Done", IsDone = true }
        },
        Tags =
        {
            new TagDef { Name = "Bug", Color = "#E5484D" },
            new TagDef { Name = "Feature", Color = "#30A46C" },
            new TagDef { Name = "Design", Color = "#8E4EC6" }
        }
    };
}

public class ColumnData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Cards in a "done" column are never reported as overdue.</summary>
    public bool IsDone { get; set; }

    /// <summary>Custom background color of the column (hex). null = theme default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    /// <summary>Custom color of the column name (hex). null = theme default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TitleColor { get; set; }

    /// <summary>Same as <see cref="Background"/>, used while the dark theme is active.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundDark { get; set; }

    /// <summary>Same as <see cref="TitleColor"/>, used while the dark theme is active.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TitleColorDark { get; set; }
    public List<CardData> Cards { get; set; } = new();
}

public class TagDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#1F6F8B";
}

public class CardData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Priority Priority { get; set; } = Priority.None;
    public string? AssigneeId { get; set; }

    /// <summary>Dependency: id of the card this one is a subtask of. null = top-level task.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; set; }

    public List<string> TagIds { get; set; } = new();
    public List<ChecklistItem> Checklist { get; set; } = new();
    /// <summary>Local wall-clock date (DateTimeKind.Unspecified), with or without a time.</summary>
    public DateTime? DueDate { get; set; }
    public bool DueHasTime { get; set; }

    /// <summary>When the task was actually completed (optional time, like the due date).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EndDate { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool EndHasTime { get; set; }

    /// <summary>Roadmap only: the day the work on the task started (no time).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartDate { get; set; }

    /// <summary>Roadmap only: replaces due and end date there, the task is drawn as one plain bar from start to finish.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FinishDate { get; set; }

    /// <summary>References: paths (inside the docs folder) of the documents linked to this task.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DocRefs { get; set; }

    public string CreatorId { get; set; } = "";
    public string CreatorName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Comment> Comments { get; set; } = new();
    public List<ActivityEntry> Activity { get; set; } = new();

    // Only set while the card sits in BoardData.Archived.
    public string? ArchivedFromColumnId { get; set; }
    public DateTime? ArchivedAt { get; set; }

    /// <summary>Copies every field from another instance (used to commit an edited draft).</summary>
    public void CopyFrom(CardData o)
    {
        Id = o.Id;
        Title = o.Title;
        Description = o.Description;
        Priority = o.Priority;
        AssigneeId = o.AssigneeId;
        ParentId = o.ParentId;
        TagIds = o.TagIds;
        Checklist = o.Checklist;
        DueDate = o.DueDate;
        DueHasTime = o.DueHasTime;
        EndDate = o.EndDate;
        EndHasTime = o.EndHasTime;
        StartDate = o.StartDate;
        FinishDate = o.FinishDate;
        DocRefs = o.DocRefs;
        CreatorId = o.CreatorId;
        CreatorName = o.CreatorName;
        CreatedAt = o.CreatedAt;
        UpdatedAt = o.UpdatedAt;
        Comments = o.Comments;
        Activity = o.Activity;
        ArchivedFromColumnId = o.ArchivedFromColumnId;
        ArchivedAt = o.ArchivedAt;
    }

    /// <summary>The moment the card is considered due (end of day when no time is set).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? DueMoment => DueDate is null ? null
        : DueHasTime ? DueDate.Value : DueDate.Value.Date.AddDays(1).AddSeconds(-1);

    /// <summary>True when the card has the dates the roadmap needs: a start plus an end or a finish date.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRoadmapDates => StartDate != null && (EndDate != null || FinishDate != null);
}

public class ChecklistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public class Comment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AuthorId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Text { get; set; } = "";
}

public class ActivityEntry
{
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Text { get; set; } = "";
}

public class SavedFilter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Search { get; set; } = "";
    public List<string> AssigneeIds { get; set; } = new();
    public List<Priority> Priorities { get; set; } = new();
    public List<string> TagIds { get; set; } = new();
    public List<string> ColumnIds { get; set; } = new();
    public DueFilter Due { get; set; } = DueFilter.Any;
    public SortMode Sort { get; set; } = SortMode.Manual;
    public bool Reverse { get; set; }
}
