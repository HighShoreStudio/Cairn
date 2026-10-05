namespace HighshoreCairn.Services;

/// <summary>Preferences of this PC (not of a project).</summary>
public class AppSettings
{
    public int Version { get; set; } = 1;
    /// <summary>"Light" or "Dark".</summary>
    public string Theme { get; set; } = "Light";
}

/// <summary>Loads and saves %APPDATA%/Cairn/settings.json.</summary>
public class SettingsService
{
    private static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try { Current = JsonFile.Load<AppSettings>(FilePath) ?? new AppSettings(); }
        catch { Current = new AppSettings(); }
    }

    public void Save()
    {
        try { JsonFile.Save(FilePath, Current); }
        catch { /* preferences are not critical */ }
    }
}
