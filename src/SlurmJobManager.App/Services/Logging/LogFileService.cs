using System.IO;
using System.IO.Compression;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.Services.Logging;

public sealed class LogFileService : ILogFileService
{
    public string LogsDirectory => LocalDataPaths.LogsDirectory;
    public string AppLogPattern => "app-*.log";

    public void EnsureLogsDirectory()
        => Directory.CreateDirectory(LogsDirectory);

    public string? GetLatestAppLogFilePath()
    {
        try
        {
            EnsureLogsDirectory();
            return Directory
                .EnumerateFiles(LogsDirectory, AppLogPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public string ReadFileText(string filePath)
        => File.ReadAllText(filePath);

    public string ExportLogsZip(string destinationZipPath)
    {
        EnsureLogsDirectory();
        var fullZipPath = Path.GetFullPath(destinationZipPath);
        var zipDir = Path.GetDirectoryName(fullZipPath);
        if (!string.IsNullOrWhiteSpace(zipDir))
            Directory.CreateDirectory(zipDir);

        if (File.Exists(fullZipPath))
            File.Delete(fullZipPath);

        try
        {
            ZipFile.CreateFromDirectory(LogsDirectory, fullZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return fullZipPath;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Unable to export logs archive: {ex.Message}", ex);
        }
    }
}
