using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>Persists and retrieves <see cref="ConnectionProfile"/> with encrypted credentials.</summary>
public interface IConnectionProfileStore
{
    /// <summary>Saves the profile, encrypting any sensitive fields before writing to disk.</summary>
    Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Loads and decrypts the stored profile, or returns <c>null</c> if none exists.</summary>
    Task<ConnectionProfile?> LoadAsync(CancellationToken ct = default);
}
