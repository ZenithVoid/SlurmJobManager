namespace SlurmJobManager.App.Services;

/// <summary>Displays lightweight non-blocking user notifications.</summary>
public interface INotificationService
{
    void Show(string title, string message, TimeSpan? expiration = null);
}
