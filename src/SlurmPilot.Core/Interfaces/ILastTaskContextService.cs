using SlurmPilot.Core.Models;

namespace SlurmPilot.Core.Interfaces;

/// <summary>
/// Persists and loads last task context records scoped by connection identity.
/// </summary>
public interface ILastTaskContextService
{
    Task<LastTaskContextRecord?> GetByConnectionAsync(string host, string username, CancellationToken ct = default);
    Task UpsertAsync(LastTaskContextRecord record, CancellationToken ct = default);
}
