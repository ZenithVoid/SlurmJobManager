namespace SlurmJobManager.App.Services.Logging;

public interface ILogFileService
{
    string LogsDirectory { get; }
    string AppLogPattern { get; }

    void EnsureLogsDirectory();
    string? GetLatestAppLogFilePath();
    string ReadFileText(string filePath);
    string ExportLogsZip(string destinationZipPath);
}
