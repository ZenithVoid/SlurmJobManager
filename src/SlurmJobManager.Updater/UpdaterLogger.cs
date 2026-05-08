namespace SlurmJobManager.Updater;

internal sealed class UpdaterLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    private UpdaterLogger(string logPath, StreamWriter writer)
    {
        LogPath = logPath;
        _writer = writer;
    }

    public string LogPath { get; }

    public static UpdaterLogger Create(string? preferredPath)
    {
        var resolvedPath = ResolvePath(preferredPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(resolvedPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        return new UpdaterLogger(resolvedPath, writer);
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private static string ResolvePath(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return Path.GetFullPath(preferredPath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlurmJobManager",
            "logs",
            "updater.log");
    }
}
