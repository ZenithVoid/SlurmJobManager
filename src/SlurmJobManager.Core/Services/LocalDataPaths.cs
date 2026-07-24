namespace SlurmJobManager.Core.Services;

/// <summary>
/// Provides unified local persistence paths under the executable directory.
/// </summary>
public static class LocalDataPaths
{
    /// <summary>
    /// Root data directory: &lt;AppBaseDirectory&gt;/Data
    /// </summary>
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>
    /// Centralized logs directory: &lt;AppBaseDirectory&gt;/Data/logs
    /// </summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Updater log file path: &lt;AppBaseDirectory&gt;/Data/logs/updater.log
    /// </summary>
    public static string UpdaterLogFilePath => Path.Combine(LogsDirectory, "updater.log");

    /// <summary>
    /// Task data root directory: &lt;AppBaseDirectory&gt;/Data/tasks
    /// </summary>
    public static string TasksDirectory => Path.Combine(DataDirectory, "tasks");

    /// <summary>
    /// Task blueprint directory: &lt;AppBaseDirectory&gt;/Data/blueprints
    /// </summary>
    public static string BlueprintsDirectory => Path.Combine(DataDirectory, "blueprints");

    /// <summary>
    /// Pinned apps/templates file path: &lt;AppBaseDirectory&gt;/Data/pins.json
    /// </summary>
    public static string PinsFilePath => Path.Combine(DataDirectory, "pins.json");

    /// <summary>
    /// App preferences file path: &lt;AppBaseDirectory&gt;/Data/preferences.json
    /// </summary>
    public static string PreferencesFilePath => Path.Combine(DataDirectory, "preferences.json");

    /// <summary>
    /// Connection profile file path: &lt;AppBaseDirectory&gt;/Data/profile.json
    /// </summary>
    public static string ProfileFilePath => Path.Combine(DataDirectory, "profile.json");

    /// <summary>
    /// Recent SSH connections file path: &lt;AppBaseDirectory&gt;/Data/recent-connections.json
    /// </summary>
    public static string RecentConnectionsFilePath => Path.Combine(DataDirectory, "recent-connections.json");

    /// <summary>
    /// Per-connection last task context file path: &lt;AppBaseDirectory&gt;/Data/last-task-contexts.json
    /// </summary>
    public static string LastTaskContextsFilePath => Path.Combine(DataDirectory, "last-task-contexts.json");

    /// <summary>
    /// Recently used and pinned task submit paths: &lt;AppBaseDirectory&gt;/Data/task-path-library.json
    /// </summary>
    public static string TaskPathLibraryFilePath => Path.Combine(DataDirectory, "task-path-library.json");
}
