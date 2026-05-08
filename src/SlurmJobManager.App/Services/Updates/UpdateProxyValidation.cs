namespace SlurmJobManager.App.Services.Updates;

internal static class UpdateProxyValidation
{
    public static bool TryValidateCustomProxy(string? host, int? port, out string? error)
    {
        error = null;
        var trimmedHost = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedHost))
        {
            error = "Custom proxy host cannot be empty.";
            return false;
        }

        if (port is null || port < 1 || port > 65535)
        {
            error = "Custom proxy port must be between 1 and 65535.";
            return false;
        }

        return true;
    }

    public static bool TryBuildCustomProxyUri(string? host, int? port, out Uri? proxyUri, out string? error)
    {
        proxyUri = null;
        if (!TryValidateCustomProxy(host, port, out error))
            return false;

        var trimmedHost = host!.Trim();
        try
        {
            proxyUri = Uri.TryCreate(trimmedHost, UriKind.Absolute, out var absolute)
                ? new UriBuilder(absolute.Scheme, absolute.Host, port!.Value).Uri
                : new UriBuilder(Uri.UriSchemeHttp, trimmedHost, port!.Value).Uri;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Custom proxy host is invalid: {ex.Message}";
            return false;
        }
    }
}
