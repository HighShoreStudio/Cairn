using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>Entry of the whiteboard list of a project.</summary>
public class WhiteboardInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Whiteboards of a project. They live next to the Kanban files:
///   whiteboards/board-{id}.json     one file per whiteboard
///   whiteboards/images/{guid}.png   pasted / dropped images (plain files: git-friendly)
/// </summary>
public class WhiteboardService
{
    public const string FolderName = "whiteboards";
    public const string ImagesFolderName = "images";

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    private readonly string _projectFolder;

    public WhiteboardService(string projectFolder) => _projectFolder = projectFolder;

    public string Folder => Path.Combine(_projectFolder, FolderName);
    public string ImagesFolder => Path.Combine(Folder, ImagesFolderName);

    private string FileOf(string id) => Path.Combine(Folder, $"board-{id}.json");

    /// <summary>All whiteboards of the project, by name.</summary>
    public List<WhiteboardInfo> List()
    {
        var result = new List<WhiteboardInfo>();
        if (!Directory.Exists(Folder)) return result;
        foreach (var file in Directory.GetFiles(Folder, "board-*.json"))
        {
            try
            {
                var data = JsonFile.Load<WhiteboardData>(file);
                if (data != null) result.Add(new WhiteboardInfo { Id = data.Id, Name = data.Name });
            }
            catch { /* skip unreadable files */ }
        }
        return result.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public WhiteboardData Create(string name)
    {
        var data = new WhiteboardData { Name = string.IsNullOrWhiteSpace(name) ? "Whiteboard" : name.Trim() };
        Save(data);
        return data;
    }

    public WhiteboardData? Load(string id) => JsonFile.Load<WhiteboardData>(FileOf(id));

    public void Save(WhiteboardData data) => JsonFile.Save(FileOf(data.Id), data);

    public void Delete(string id)
    {
        var file = FileOf(id);
        if (File.Exists(file)) File.Delete(file);
    }

    // ------------------------------------------------------------------ images

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public string ImagePath(string fileName) => Path.Combine(ImagesFolder, fileName);

    /// <summary>Copies an external image into the project and returns its new file name.</summary>
    public string ImportImage(string sourcePath)
    {
        Directory.CreateDirectory(ImagesFolder);
        var name = Guid.NewGuid().ToString("N")[..16] + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, ImagePath(name), overwrite: false);
        return name;
    }

    /// <summary>Stores image bytes (for example a pasted screenshot encoded as PNG) and returns the file name.</summary>
    public string SaveImage(byte[] bytes, string extension = ".png")
    {
        Directory.CreateDirectory(ImagesFolder);
        var name = Guid.NewGuid().ToString("N")[..16] + extension;
        File.WriteAllBytes(ImagePath(name), bytes);
        return name;
    }

    /// <summary>Deletes the image files that no whiteboard uses any more. Returns how many were removed.</summary>
    public int CleanupImages()
    {
        if (!Directory.Exists(ImagesFolder)) return 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in List())
        {
            WhiteboardData? data;
            try { data = Load(info.Id); }
            catch { return 0; } // a board cannot be read: do not risk deleting its images
            if (data is null) continue;
            foreach (var file in WhiteboardOps.ImageFiles(data.Elements)) used.Add(file);
        }

        // A board file that failed to parse in List() would hide its images: be conservative.
        if (Directory.GetFiles(Folder, "board-*.json").Length != List().Count) return 0;

        var removed = 0;
        foreach (var file in Directory.GetFiles(ImagesFolder))
        {
            if (used.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); removed++; } catch { /* in use: try next time */ }
        }
        return removed;
    }
}
