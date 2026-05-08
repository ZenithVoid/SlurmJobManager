using System.Text;
using System.Text.Json;

namespace SlurmJobManager.Core.Models;

public enum UpdatePackageType
{
    Zip = 0,
    Installer = 1,
}

public sealed record UpdaterLaunchRequest(
    int ParentProcessId,
    string MainExecutablePath,
    string InstallDirectory,
    string UpdatePackagePath,
    UpdatePackageType PackageType,
    bool RestartMainApplication,
    string? RestartArguments,
    string? LogFilePath);

public static class UpdaterLaunchContract
{
    private const string RequestArgName = "--request";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string BuildCommandLineArguments(UpdaterLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"{RequestArgName} \"{SerializeToBase64(request)}\"";
    }

    public static bool TryParse(string[] args, out UpdaterLaunchRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (args is null || args.Length == 0)
        {
            error = "Missing updater request argument.";
            return false;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], RequestArgName, StringComparison.OrdinalIgnoreCase))
                continue;

            return TryDeserializeFromBase64(args[i + 1], out request, out error);
        }

        error = $"Missing required argument: {RequestArgName}.";
        return false;
    }

    public static string SerializeToBase64(UpdaterLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDeserializeFromBase64(string payload, out UpdaterLaunchRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Updater request payload is empty.";
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            request = JsonSerializer.Deserialize<UpdaterLaunchRequest>(json, JsonOptions);
            if (request is null)
            {
                error = "Updater request payload is invalid.";
                return false;
            }

            if (request.ParentProcessId <= 0)
            {
                error = "Parent process ID must be greater than zero.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.MainExecutablePath))
            {
                error = "Main executable path is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.InstallDirectory))
            {
                error = "Install directory is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.UpdatePackagePath))
            {
                error = "Update package path is required.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse updater request payload: {ex.Message}";
            return false;
        }
    }
}
