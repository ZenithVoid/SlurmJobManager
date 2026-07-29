using System.IO;
using System.Text.Json;
using SlurmPilot.Core.Models;
using SlurmPilot.Core.Services;

namespace SlurmPilot.App.Services;

public sealed class TaskPathLibraryService
{
    private const int MaxRecentPerKind = 40;
    private static readonly Lazy<TaskPathLibraryService> LazyInstance = new(() => new TaskPathLibraryService());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private TaskPathLibraryData _data;

    public static TaskPathLibraryService Instance => LazyInstance.Value;

    private TaskPathLibraryService()
    {
        _data = Load();
    }

    public IReadOnlyList<TaskPathLibraryEntry> GetEntries(TaskPathKind kind)
    {
        lock (_syncRoot)
        {
            return GetList(kind)
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .OrderByDescending(x => x.IsFavorite)
                .ThenByDescending(x => x.LastUsedAtUtc)
                .Select(Clone)
                .ToList();
        }
    }

    public bool IsFavorite(TaskPathKind kind, string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        lock (_syncRoot)
        {
            return GetList(kind).FirstOrDefault(x => SamePath(x.Path, normalized))?.IsFavorite == true;
        }
    }

    public void Remember(TaskPathKind kind, string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        lock (_syncRoot)
        {
            var list = GetList(kind);
            var entry = list.FirstOrDefault(x => SamePath(x.Path, normalized));
            if (entry == null)
            {
                entry = new TaskPathLibraryEntry { Path = normalized };
                list.Add(entry);
            }

            entry.LastUsedAtUtc = DateTime.UtcNow;
            TrimList(list);
            Save();
        }
    }

    public bool ToggleFavorite(TaskPathKind kind, string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        lock (_syncRoot)
        {
            var list = GetList(kind);
            var entry = list.FirstOrDefault(x => SamePath(x.Path, normalized));
            if (entry == null)
            {
                entry = new TaskPathLibraryEntry { Path = normalized, LastUsedAtUtc = DateTime.UtcNow };
                list.Add(entry);
            }

            entry.IsFavorite = !entry.IsFavorite;
            if (entry.IsFavorite)
                entry.LastUsedAtUtc = DateTime.UtcNow;

            TrimList(list);
            Save();
            return entry.IsFavorite;
        }
    }

    public void RememberCommands(IEnumerable<CommandEntry> commands)
    {
        foreach (var command in commands)
        {
            Remember(TaskPathKind.Program, command.ProgramPath);
            Remember(TaskPathKind.Program, command.PythonInterpreterPath);
            foreach (var file in command.ParameterFiles)
                Remember(TaskPathKind.ParameterFile, file);
        }
    }

    public void RememberBlueprint(TaskBlueprintRecord blueprint)
    {
        foreach (var unit in blueprint.TaskUnits)
        {
            foreach (var program in unit.ProgramEntries)
                Remember(TaskPathKind.Program, program.ProgramPath);
            foreach (var file in unit.ParameterFiles)
                Remember(TaskPathKind.ParameterFile, file.FilePath);
            RememberCommands(unit.CommandEntries);
        }
    }

    private List<TaskPathLibraryEntry> GetList(TaskPathKind kind)
        => kind == TaskPathKind.Program ? _data.Programs : _data.ParameterFiles;

    private static void TrimList(List<TaskPathLibraryEntry> list)
    {
        var recent = list
            .Where(x => !x.IsFavorite)
            .OrderByDescending(x => x.LastUsedAtUtc)
            .Skip(MaxRecentPerKind)
            .ToHashSet();

        list.RemoveAll(recent.Contains);
    }

    private static TaskPathLibraryEntry Clone(TaskPathLibraryEntry source)
        => new()
        {
            Path = source.Path,
            LastUsedAtUtc = source.LastUsedAtUtc,
            IsFavorite = source.IsFavorite,
        };

    private static bool SamePath(string? left, string? right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

    private static TaskPathLibraryData Load()
    {
        try
        {
            if (!File.Exists(LocalDataPaths.TaskPathLibraryFilePath))
                return new TaskPathLibraryData();

            var json = File.ReadAllText(LocalDataPaths.TaskPathLibraryFilePath);
            return JsonSerializer.Deserialize<TaskPathLibraryData>(json, JsonOptions) ?? new TaskPathLibraryData();
        }
        catch
        {
            return new TaskPathLibraryData();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(LocalDataPaths.DataDirectory);
        File.WriteAllText(LocalDataPaths.TaskPathLibraryFilePath, JsonSerializer.Serialize(_data, JsonOptions));
    }
}

public sealed class TaskPathLibraryData
{
    public List<TaskPathLibraryEntry> Programs { get; set; } = new();
    public List<TaskPathLibraryEntry> ParameterFiles { get; set; } = new();
}

public sealed class TaskPathLibraryEntry
{
    public string Path { get; set; } = string.Empty;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsFavorite { get; set; }
}
