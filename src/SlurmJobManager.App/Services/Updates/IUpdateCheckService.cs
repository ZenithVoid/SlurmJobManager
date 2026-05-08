namespace SlurmJobManager.App.Services.Updates;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default);
    Task<UpdateConnectivityTestResult> TestConnectivityAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default);
}
