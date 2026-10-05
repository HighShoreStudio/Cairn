namespace HighshoreCairn.Services;

/// <summary>Well-known locations of the application data.</summary>
public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>%APPDATA%/Cairn (can be redirected for tests or portable use with the CAIRN_HOME
    /// environment variable). The app was called "Highshore Kanban" before: when its data folder
    /// (%APPDATA%/HighshoreKanban) is already on this PC and the new one is not, it keeps being used,
    /// so accounts and projects are still there after the rename.</summary>
    public static string Root
    {
        get
        {
            if (_rootOverride != null) return _rootOverride;
            var env = Environment.GetEnvironmentVariable("CAIRN_HOME");
            if (string.IsNullOrWhiteSpace(env)) env = Environment.GetEnvironmentVariable("HIGHSHORE_KANBAN_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var current = Path.Combine(appData, "Cairn");
            var legacy = Path.Combine(appData, "HighshoreKanban");
            return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
        }
    }

    public static void OverrideRoot(string? path) => _rootOverride = path;

    public static string AccountsFile => Path.Combine(Root, "accounts.json");
    public static string SessionFile => Path.Combine(Root, "session.dat");
    public static string RegistryFile => Path.Combine(Root, "projects.json");
    public static string ProjectsDir => Path.Combine(Root, "projects");
}
