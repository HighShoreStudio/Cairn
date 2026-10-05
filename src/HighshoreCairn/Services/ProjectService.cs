using System.Text;
using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>Information about one automatic backup of kanban.json.</summary>
public class BackupInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long Size { get; set; }
    public string Display => $"{CreatedAt:yyyy-MM-dd HH:mm:ss}   ({Math.Max(1, Size / 1024)} KB)";
}

/// <summary>
/// File-based storage of projects. A project is a folder:
///   project.json   metadata
///   kanban.json    board state (columns + cards)
///   accounts.json  accounts authorized in the project
///   backups/       automatic backups of kanban.json (last 5)
///   .gitkeep
/// Everything is plain, indented JSON: no lock files, git-friendly.
/// </summary>
public class ProjectService
{
    public const string ProjectFileName = "project.json";
    public const string BoardFileName = "kanban.json";
    public const string AccountsFileName = "accounts.json";
    public const string BackupsDirName = "backups";
    public const int MaxBackups = 5;

    private ProjectRegistry _registry = new();

    public ProjectService() => LoadRegistry();

    // ------------------------------------------------------------------ registry (this PC only)

    private void LoadRegistry()
    {
        try { _registry = JsonFile.Load<ProjectRegistry>(AppPaths.RegistryFile) ?? new ProjectRegistry(); }
        catch { _registry = new ProjectRegistry(); }
    }

    private void SaveRegistry() => JsonFile.Save(AppPaths.RegistryFile, _registry);

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private ProjectRef Register(string folder)
    {
        var existing = _registry.Projects.FirstOrDefault(p => SamePath(p.Path, folder));
        if (existing != null) return existing;
        var reference = new ProjectRef { Path = Normalize(folder) };
        _registry.Projects.Add(reference);
        SaveRegistry();
        return reference;
    }

    public void MarkOpened(string folder)
    {
        Register(folder).LastOpenedAt = DateTime.UtcNow;
        SaveRegistry();
    }

    /// <summary>Removes the project from the list without touching its files.</summary>
    public void Forget(string folder)
    {
        _registry.Projects.RemoveAll(p => SamePath(p.Path, folder));
        SaveRegistry();
    }

    // ------------------------------------------------------------------ listing

    public static bool IsProjectFolder(string folder) =>
        File.Exists(System.IO.Path.Combine(folder, ProjectFileName));

    /// <summary>All projects known on this PC: the registered ones + those found in the default folder.</summary>
    public List<ProjectEntry> List()
    {
        var folders = new List<string>();
        void Add(string f)
        {
            if (!folders.Any(x => SamePath(x, f))) folders.Add(Normalize(f));
        }

        foreach (var p in _registry.Projects) Add(p.Path);
        if (Directory.Exists(AppPaths.ProjectsDir))
            foreach (var dir in Directory.GetDirectories(AppPaths.ProjectsDir))
                if (IsProjectFolder(dir)) Add(dir);

        var result = new List<ProjectEntry>();
        foreach (var folder in folders)
        {
            var entry = TryLoadEntry(folder);
            if (entry != null) result.Add(entry);
        }

        return result
            .OrderByDescending(e => e.LastOpenedAt ?? DateTime.MinValue)
            .ThenBy(e => e.Info.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ProjectEntry? TryLoadEntry(string folder)
    {
        try
        {
            if (!IsProjectFolder(folder)) return null;
            var info = JsonFile.Load<ProjectInfo>(System.IO.Path.Combine(folder, ProjectFileName));
            if (info is null) return null;
            var entry = new ProjectEntry
            {
                Folder = Normalize(folder),
                Info = info,
                LastOpenedAt = _registry.Projects.FirstOrDefault(p => SamePath(p.Path, folder))?.LastOpenedAt
            };
            try
            {
                var board = LoadBoard(folder);
                entry.ColumnCount = board.Columns.Count;
                entry.CardCount = board.Columns.Sum(c => c.Cards.Count);
                entry.MemberCount = LoadAccounts(folder).Accounts.Count;
            }
            catch { /* counters are optional */ }
            return entry;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ create / delete

    /// <summary>Turns a project name into a safe folder name ("My Game!" -> "my-game").</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "project" : slug;
    }

    public static string DefaultFolderFor(string name) =>
        System.IO.Path.Combine(AppPaths.ProjectsDir, Slug(name));

    // ------------------------------------------------------------------ default template

    private static string TemplateFile => System.IO.Path.Combine(AppPaths.Root, "template.json");

    /// <summary>The customization used for new projects, or null when the built-in defaults apply.</summary>
    public ProjectTemplate? LoadTemplate()
    {
        try { return JsonFile.Load<ProjectTemplate>(TemplateFile); }
        catch { return null; }
    }

    /// <summary>
    /// Makes the customization of a project the default for new projects: columns (names, order,
    /// colors, done flag), tags, saved filters, timer, documentation and roadmap settings (with the
    /// milestone types and their icons), project color.
    /// Cards, archived cards, whiteboards and documents are never part of the template.
    /// </summary>
    public ProjectTemplate SaveTemplate(ProjectEntry entry, string? color = null, ProjectSettings? settings = null)
    {
        var board = LoadBoard(entry.Folder);
        var template = new ProjectTemplate
        {
            SourceProjectId = entry.Info.Id,
            SourceProjectName = entry.Info.Name,
            Color = color ?? entry.Info.Color,
            Columns = board.Columns.Select(c => new ColumnData
            {
                Id = c.Id, Name = c.Name, IsDone = c.IsDone,
                Background = c.Background, TitleColor = c.TitleColor,
                BackgroundDark = c.BackgroundDark, TitleColorDark = c.TitleColorDark
            }).ToList(),
            Tags = JsonFile.Clone(board.Tags),
            SavedFilters = JsonFile.Clone(board.SavedFilters),
            Settings = JsonFile.Clone(settings ?? entry.Info.Settings)
        };
        JsonFile.Save(TemplateFile, template);

        // The icon pictures of the milestone types travel with the template.
        ClearTemplateIcons();
        try
        {
            RoadmapService.CopyIcons(template.Settings.Roadmap, new RoadmapService(entry.Folder).IconsFolder, TemplateIconsFolder);
        }
        catch { /* a missing picture only means the default icon is shown */ }
        return template;
    }

    /// <summary>Copies of the custom icon pictures used by the default template.</summary>
    public static string TemplateIconsFolder => System.IO.Path.Combine(AppPaths.Root, "template-icons");

    private static void ClearTemplateIcons()
    {
        try
        {
            if (!Directory.Exists(TemplateIconsFolder)) return;
            foreach (var file in Directory.GetFiles(TemplateIconsFolder)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(TemplateIconsFolder).Any()) Directory.Delete(TemplateIconsFolder);
        }
        catch { /* leftovers are harmless */ }
    }

    /// <summary>Back to the original (built-in) defaults for new projects.</summary>
    public void ClearTemplate()
    {
        if (File.Exists(TemplateFile)) File.Delete(TemplateFile);
        ClearTemplateIcons();
    }

    private static BoardData BoardFromTemplate(ProjectTemplate template)
    {
        if (template.Columns.Count == 0) return BoardData.CreateDefault();
        return new BoardData
        {
            Columns = JsonFile.Clone(template.Columns),
            Tags = JsonFile.Clone(template.Tags),
            SavedFilters = JsonFile.Clone(template.SavedFilters)
        };
    }

    public ProjectEntry Create(string name, string description, string? folder, string color, Account owner, string? icon = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("The project name is required.");

        folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolderFor(name) : folder.Trim();
        if (Directory.Exists(folder))
        {
            if (IsProjectFolder(folder))
                throw new InvalidOperationException("That folder already contains a project. Use 'Open Folder' to add it to the list.");
            if (Directory.EnumerateFileSystemEntries(folder).Any())
                throw new InvalidOperationException("That folder is not empty. Choose an empty or a new folder.");
        }

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(System.IO.Path.Combine(folder, BackupsDirName));

        var template = LoadTemplate();
        var info = new ProjectInfo
        {
            Name = name,
            Description = (description ?? "").Trim(),
            Color = color,
            Icon = icon,
            OwnerId = owner.Id,
            OwnerName = owner.Username,
            Settings = template != null ? JsonFile.Clone(template.Settings) : new ProjectSettings()
        };
        SaveInfo(folder, info);
        if (template != null)
        {
            try { RoadmapService.CopyIcons(info.Settings.Roadmap, TemplateIconsFolder, new RoadmapService(folder).IconsFolder); }
            catch { /* the default icon is shown for a picture that could not be copied */ }
        }
        SaveBoard(folder, template != null ? BoardFromTemplate(template) : BoardData.CreateDefault());
        SaveAccounts(folder, new AccountStore { Accounts = { owner.CloneForProject() } });
        File.WriteAllText(System.IO.Path.Combine(folder, ".gitkeep"), "");
        File.WriteAllText(System.IO.Path.Combine(folder, BackupsDirName, ".gitkeep"), "");

        Register(folder);
        return TryLoadEntry(folder)!;
    }

    /// <summary>Registers an existing project folder (for example one cloned from git).</summary>
    public ProjectEntry AddExisting(string folder)
    {
        if (!IsProjectFolder(folder))
            throw new InvalidOperationException("The selected folder does not contain a project.json file.");
        Register(folder);
        return TryLoadEntry(folder)!;
    }

    /// <summary>
    /// Deletes the project files. Only files created by the app are removed; the folder itself
    /// is removed only if nothing else is left inside (so a .git folder or other files are preserved).
    /// </summary>
    public void Delete(string folder)
    {
        Forget(folder);
        if (!Directory.Exists(folder)) return;

        // The default template is a copy: deleting its source project does not remove it.
        foreach (var name in new[] { ProjectFileName, BoardFileName, AccountsFileName, RoadmapService.FileName, ".gitkeep" })
        {
            var file = System.IO.Path.Combine(folder, name);
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp");
        }

        var backups = System.IO.Path.Combine(folder, BackupsDirName);
        if (Directory.Exists(backups))
        {
            foreach (var file in Directory.GetFiles(backups, "kanban-*.json")) File.Delete(file);
            var keep = System.IO.Path.Combine(backups, ".gitkeep");
            if (File.Exists(keep)) File.Delete(keep);
            if (!Directory.EnumerateFileSystemEntries(backups).Any()) Directory.Delete(backups);
        }

        // Whiteboards: board files and the images copied into the project.
        var whiteboards = System.IO.Path.Combine(folder, WhiteboardService.FolderName);
        if (Directory.Exists(whiteboards))
        {
            foreach (var file in Directory.GetFiles(whiteboards, "board-*.json")) File.Delete(file);
            var images = System.IO.Path.Combine(whiteboards, WhiteboardService.ImagesFolderName);
            if (Directory.Exists(images))
            {
                foreach (var file in Directory.GetFiles(images).Where(WhiteboardService.IsImageFile)) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(images).Any()) Directory.Delete(images);
            }
            if (!Directory.EnumerateFileSystemEntries(whiteboards).Any()) Directory.Delete(whiteboards);
        }

        // Roadmap: the pictures chosen as icons of the milestone types.
        var icons = System.IO.Path.Combine(folder, RoadmapService.IconsFolderName);
        if (Directory.Exists(icons))
        {
            foreach (var file in Directory.GetFiles(icons)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(icons).Any()) Directory.Delete(icons);
        }

        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    // ------------------------------------------------------------------ project files

    public void SaveInfo(string folder, ProjectInfo info) =>
        JsonFile.Save(System.IO.Path.Combine(folder, ProjectFileName), info);

    public BoardData LoadBoard(string folder)
    {
        var board = JsonFile.Load<BoardData>(System.IO.Path.Combine(folder, BoardFileName)) ?? BoardData.CreateDefault();
        Migrate(board);
        return board;
    }

    /// <summary>Upgrades a board written by an older version of the app.</summary>
    public static void Migrate(BoardData board)
    {
        if (board.Version >= 2) return;
        // Up to v1.1 a column had a single color used by both themes: keep that look in the dark theme too.
        foreach (var column in board.Columns)
        {
            column.BackgroundDark ??= column.Background;
            column.TitleColorDark ??= column.TitleColor;
        }
        board.Version = 2;
    }

    public void SaveBoard(string folder, BoardData board) =>
        JsonFile.Save(System.IO.Path.Combine(folder, BoardFileName), board);

    public AccountStore LoadAccounts(string folder) =>
        JsonFile.Load<AccountStore>(System.IO.Path.Combine(folder, AccountsFileName)) ?? new AccountStore();

    public void SaveAccounts(string folder, AccountStore store)
    {
        // Session tokens never leave the global archive.
        foreach (var account in store.Accounts) account.SessionTokenHashes = null;
        JsonFile.Save(System.IO.Path.Combine(folder, AccountsFileName), store);
    }

    // ------------------------------------------------------------------ backups

    /// <summary>
    /// Copies kanban.json into backups/ (kanban-yyyy-MM-ddTHH-mm-ss.json) and keeps only the newest 5.
    /// Nothing is written when the board is identical to the latest backup.
    /// </summary>
    public string? Backup(string folder)
    {
        var source = System.IO.Path.Combine(folder, BoardFileName);
        if (!File.Exists(source)) return null;

        var dir = System.IO.Path.Combine(folder, BackupsDirName);
        Directory.CreateDirectory(dir);

        var latest = ListBackups(folder).FirstOrDefault();
        if (latest != null && FilesEqual(latest.Path, source)) return latest.Path;

        var target = System.IO.Path.Combine(dir, $"kanban-{DateTime.Now:yyyy-MM-ddTHH-mm-ss}.json");
        File.Copy(source, target, overwrite: true);

        foreach (var old in ListBackups(folder).Skip(MaxBackups))
        {
            try { File.Delete(old.Path); } catch { /* best effort */ }
        }
        return target;
    }

    /// <summary>Backups, newest first.</summary>
    public List<BackupInfo> ListBackups(string folder)
    {
        var dir = System.IO.Path.Combine(folder, BackupsDirName);
        if (!Directory.Exists(dir)) return new List<BackupInfo>();
        return Directory.GetFiles(dir, "kanban-*.json")
            .Select(f => new FileInfo(f))
            // The timestamp is in the file name, so the name order is the chronological order
            // (file dates are not reliable after a git checkout).
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupInfo
            {
                Path = f.FullName,
                FileName = f.Name,
                CreatedAt = ParseBackupTime(f.Name) ?? f.LastWriteTime,
                Size = f.Length
            })
            .ToList();
    }

    private static DateTime? ParseBackupTime(string fileName)
    {
        var stamp = System.IO.Path.GetFileNameWithoutExtension(fileName);
        if (stamp.StartsWith("kanban-")) stamp = stamp["kanban-".Length..];
        return DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var value) ? value : null;
    }

    /// <summary>Replaces kanban.json with a backup (the current board is backed up first).</summary>
    public void RestoreBackup(string folder, BackupInfo backup)
    {
        // Validate before overwriting anything.
        var restored = JsonFile.Load<BoardData>(backup.Path)
                       ?? throw new InvalidOperationException("The backup file is empty or invalid.");
        var content = File.ReadAllText(backup.Path, Encoding.UTF8);
        Backup(folder);
        _ = restored;
        JsonFile.WriteAllTextAtomic(System.IO.Path.Combine(folder, BoardFileName), content);
    }

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }
}
