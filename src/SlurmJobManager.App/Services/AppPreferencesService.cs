using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.Services;

/// <summary>
/// Persists application-level user preferences (e.g. startup auto-connect) to a JSON file
/// under <c>&lt;AppBaseDirectory&gt;/Data/preferences.json</c>.
/// </summary>
public sealed class AppPreferencesService
{
    private static readonly string PrefsPath = LocalDataPaths.PreferencesFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private AppPrefsDto _dto;

    public AppPreferencesService()
    {
        _dto = Load();
    }

    /// <summary>
    /// When <c>true</c> the app will automatically invoke ConnectCommand after loading a saved
    /// profile on startup.
    /// </summary>
    public bool AutoConnectOnStartup
    {
        get => _dto.AutoConnectOnStartup;
        set
        {
            if (_dto.AutoConnectOnStartup == value) return;
            _dto = _dto with { AutoConnectOnStartup = value };
            Save();
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private AppPrefsDto Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var json = File.ReadAllText(PrefsPath);
                return JsonSerializer.Deserialize<AppPrefsDto>(json, JsonOptions) ?? new AppPrefsDto();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppPreferencesService.Load] {ex.Message}");
        }
        return new AppPrefsDto();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_dto, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppPreferencesService.Save] {ex.Message}");
        }
    }

    // ── DTO ──────────────────────────────────────────────────────────────────

    private sealed record AppPrefsDto(bool AutoConnectOnStartup = false);
}
