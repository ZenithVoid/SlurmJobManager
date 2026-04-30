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

    public static string TasksDirectory => Path.Combine(DataDirectory, "tasks");

    public static string BlueprintsDirectory => Path.Combine(DataDirectory, "Blueprints");

    public static string PinsFilePath => Path.Combine(DataDirectory, "pins.json");

    public static string PreferencesFilePath => Path.Combine(DataDirectory, "preferences.json");

    public static string ProfileFilePath => Path.Combine(DataDirectory, "profile.json");
}
