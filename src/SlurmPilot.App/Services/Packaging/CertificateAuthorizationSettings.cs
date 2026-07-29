using System.Security.Cryptography.X509Certificates;

namespace SlurmPilot.App.Services.Packaging;

public static class CertificateAuthorizationSettings
{
    public const string Thumbprint = "49E6B36E13F3AB5C3514A196A23E92C7F284404D";
    public const StoreLocation StoreLocation = StoreLocation.CurrentUser;
    public const StoreName StoreName = StoreName.My;
    public const bool RequirePrivateKey = true;
    public const bool RequireValidDate = true;
    public const string Subject = "CN=SlurmPilot Packaging Authorization";
}
