using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>Persists and manages non-sensitive recent SSH connections.</summary>
public interface IRecentConnectionService
{
    Task<IReadOnlyList<RecentConnectionRecord>> GetRecentAsync(CancellationToken ct = default);
    Task AddOrUpdateAsync(RecentConnectionRecord record, CancellationToken ct = default);
    Task RemoveAsync(string host, int port, string username, CancellationToken ct = default);
}
