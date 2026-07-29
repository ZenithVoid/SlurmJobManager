using System.Security.Cryptography.X509Certificates;
using SlurmPilot.Core.Interfaces;

namespace SlurmPilot.App.Services.Packaging;

public sealed class PackagingFeatureAuthorizationService : IPackagingFeatureAuthorizationService
{
    private readonly IAppLogger? _logger;
    private PackagingFeatureAuthorizationResult? _cachedResult;

    public PackagingFeatureAuthorizationService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public PackagingFeatureAuthorizationResult EvaluateAuthorization()
    {
        if (_cachedResult is not null)
            return _cachedResult;

        _logger?.Info("Starting packaging authorization certificate check.");

        try
        {
            var targetThumbprint = NormalizeThumbprint(CertificateAuthorizationSettings.Thumbprint);
            using var store = new X509Store(CertificateAuthorizationSettings.StoreName, CertificateAuthorizationSettings.StoreLocation);
            store.Open(OpenFlags.ReadOnly);

            var now = DateTimeOffset.Now;
            foreach (var certificate in store.Certificates.Cast<X509Certificate2>())
            {
                var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
                if (!string.Equals(thumbprint, targetThumbprint, StringComparison.OrdinalIgnoreCase))
                    continue;

                _logger?.Info($"Packaging authorization certificate thumbprint matched in CurrentUser\\My. Subject={certificate.Subject}");

                if (CertificateAuthorizationSettings.RequireValidDate &&
                    (now < certificate.NotBefore || now > certificate.NotAfter))
                {
                    _logger?.Warning("Packaging authorization certificate exists but is not currently valid by date.");
                    return _cachedResult = new PackagingFeatureAuthorizationResult(false, "Certificate is expired or not yet valid.");
                }

                if (CertificateAuthorizationSettings.RequirePrivateKey && !certificate.HasPrivateKey)
                {
                    _logger?.Warning("Packaging authorization certificate exists but private key is missing.");
                    return _cachedResult = new PackagingFeatureAuthorizationResult(false, "Certificate private key is required.");
                }

                if (!string.Equals(
                        certificate.Subject?.Trim(),
                        CertificateAuthorizationSettings.Subject,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.Warning(
                        $"Packaging authorization certificate subject did not match optional hint. Expected={CertificateAuthorizationSettings.Subject}, Actual={certificate.Subject}");
                }

                _logger?.Info("Packaging authorization certificate verification passed.");
                return _cachedResult = new PackagingFeatureAuthorizationResult(true, "Authorized");
            }

            _logger?.Warning("Packaging authorization certificate was not found in CurrentUser\\My.");
            return _cachedResult = new PackagingFeatureAuthorizationResult(false, "Matching certificate not found.");
        }
        catch (Exception ex)
        {
            _logger?.Error("Packaging authorization certificate check failed.", ex);
            return _cachedResult = new PackagingFeatureAuthorizationResult(false, ex.Message);
        }
    }

    private static string NormalizeThumbprint(string? thumbprint)
        => new((thumbprint ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
}
