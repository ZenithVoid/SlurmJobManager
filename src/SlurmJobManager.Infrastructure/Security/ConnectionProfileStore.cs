using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.Infrastructure.Security;

/// <summary>
/// Stores a <see cref="ConnectionProfile"/> as JSON under
/// <c>&lt;AppBaseDirectory&gt;/Data/profile.json</c>.
/// Sensitive fields (password and key passphrase) are encrypted via
/// <see cref="ICredentialProtector"/> before writing and decrypted on load.
/// </summary>
public sealed class ConnectionProfileStore : IConnectionProfileStore
{
    private static readonly string ProfilePath = LocalDataPaths.ProfileFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICredentialProtector _protector;
    private readonly IAppLogger _logger;

    public ConnectionProfileStore(ICredentialProtector protector, IAppLogger logger)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var dto = new ProfileDto
        {
            Label             = profile.Label,
            Host              = profile.Host,
            Port              = profile.Port,
            Username          = profile.Username,
            PrivateKeyPath    = profile.PrivateKeyPath,
            // Encrypt sensitive fields; tolerate null/empty gracefully
            PasswordProtected          = EncryptOptional(profile.Password),
            PassphraseProtected        = EncryptOptional(profile.PrivateKeyPassphrase),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await File.WriteAllTextAsync(ProfilePath, json, ct);
        _logger.Info($"Connection profile saved for {profile.Username}@{profile.Host}");
    }

    /// <inheritdoc/>
    public async Task<ConnectionProfile?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ProfilePath)) return null;

        var json = await File.ReadAllTextAsync(ProfilePath, ct);
        var dto  = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions);
        if (dto is null) return null;

        var profile = new ConnectionProfile
        {
            Label          = dto.Label ?? string.Empty,
            Host           = dto.Host  ?? string.Empty,
            Port           = dto.Port,
            Username       = dto.Username ?? string.Empty,
            PrivateKeyPath = dto.PrivateKeyPath,
        };

        // Decrypt sensitive fields
        if (!string.IsNullOrEmpty(dto.PasswordProtected))
            profile.Password = DecryptOptional(dto.PasswordProtected, nameof(dto.PasswordProtected));

        if (!string.IsNullOrEmpty(dto.PassphraseProtected))
            profile.PrivateKeyPassphrase = DecryptOptional(dto.PassphraseProtected, nameof(dto.PassphraseProtected));

        _logger.Info($"Connection profile loaded for {profile.Username}@{profile.Host}");
        return profile;
    }

    private string? EncryptOptional(string? value)
        => string.IsNullOrEmpty(value) ? null : _protector.Protect(value);

    private string? DecryptOptional(string cipherText, string fieldName)
    {
        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning($"Failed to decrypt field '{fieldName}': {ex.Message}. The field will be cleared.");
            return null;
        }
    }

    // ── DTO ──────────────────────────────────────────────────────────────────

    private sealed class ProfileDto
    {
        public string? Label                 { get; set; }
        public string? Host                  { get; set; }
        public int     Port                  { get; set; } = 22;
        public string? Username              { get; set; }
        public string? PrivateKeyPath        { get; set; }
        /// <summary>DPAPI-encrypted password (Base-64).</summary>
        public string? PasswordProtected     { get; set; }
        /// <summary>DPAPI-encrypted key passphrase (Base-64).</summary>
        public string? PassphraseProtected   { get; set; }
    }
}
