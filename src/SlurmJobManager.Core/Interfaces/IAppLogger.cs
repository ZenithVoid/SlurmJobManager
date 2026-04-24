namespace SlurmJobManager.Core.Interfaces;

/// <summary>Application-level structured logger abstraction.</summary>
public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
}
