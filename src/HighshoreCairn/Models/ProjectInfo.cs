using System.Text.Json.Serialization;

namespace HighshoreCairn.Models;

/// <summary>Settings of the tomato (pomodoro) timer of a project.</summary>
public class PomodoroSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    /// <summary>A long break replaces the short one every N focus sessions.</summary>
    public int LongBreakEvery { get; set; } = 4;
    public bool AutoStartBreaks { get; set; } = true;
    public bool AutoStartFocus { get; set; }
    /// <summary>"None", "Chime", "Bell", "Digital" or "Custom" (a .wav file).</summary>
    public string Sound { get; set; } = "Chime";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomSoundFile { get; set; }
    /// <summary>0-100.</summary>
    public int Volume { get; set; } = 80;
    /// <summary>Hides the timer from the project header.</summary>
    public bool Hidden { get; set; }
}

public enum DocImportMode { Copy, Link, Ask }

/// <summary>Settings of the Documentation area of a project.</summary>
public class DocsSettings
{
    /// <summary>What happens to external files added to the documentation: copied into docs/, linked, or asked each time.</summary>
    public DocImportMode ImportMode { get; set; } = DocImportMode.Copy;
    /// <summary>Documents open in the formatted view (true) or in the markdown source view (false).</summary>
    public bool OpenInPreview { get; set; } = true;
}

/// <summary>Per-project customization (stored in project.json, copied by the default template).</summary>
public class ProjectSettings
{
    public PomodoroSettings Pomodoro { get; set; } = new();
    public DocsSettings Docs { get; set; } = new();
    public RoadmapSettings Roadmap { get; set; } = new();
}

/// <summary>
/// Content of %APPDATA%/Cairn/template.json: the customization of the project chosen as
/// "default template". New projects start from it. It never contains cards, whiteboards or documents.
/// </summary>
public class ProjectTemplate
{
    public int Version { get; set; } = 1;
    public string SourceProjectId { get; set; } = "";
    public string SourceProjectName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Color { get; set; } = "#1F6F8B";
    public List<ColumnData> Columns { get; set; } = new();
    public List<TagDef> Tags { get; set; } = new();
    public List<SavedFilter> SavedFilters { get; set; } = new();
    public ProjectSettings Settings { get; set; } = new();
}

/// <summary>Content of project.json: project metadata.</summary>
public class ProjectInfo
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#1F6F8B";

    /// <summary>Optional project icon: a small square PNG stored as base64.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string OwnerId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public ProjectSettings Settings { get; set; } = new();
}

/// <summary>Pointer to a project folder known on this PC (kept outside the project, so it never dirties git).</summary>
public class ProjectRef
{
    public string Path { get; set; } = "";
    public DateTime? LastOpenedAt { get; set; }
}

/// <summary>Content of %APPDATA%/Cairn/projects.json.</summary>
public class ProjectRegistry
{
    public int Version { get; set; } = 1;
    public List<ProjectRef> Projects { get; set; } = new();
}

/// <summary>A project as shown in the project list (metadata + a few counters).</summary>
public class ProjectEntry
{
    public string Folder { get; set; } = "";
    public ProjectInfo Info { get; set; } = new();
    public DateTime? LastOpenedAt { get; set; }
    public int CardCount { get; set; }
    public int ColumnCount { get; set; }
    public int MemberCount { get; set; }
}
