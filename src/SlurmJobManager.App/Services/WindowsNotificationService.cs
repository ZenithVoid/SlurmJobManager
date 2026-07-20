using System.Diagnostics;
using System.Security;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.Services;

/// <summary>Windows native toast notification implementation.</summary>
public sealed class WindowsNotificationService : INotificationService
{
    private readonly IAppLogger? _logger;

    public WindowsNotificationService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public void Show(string title, string message, TimeSpan? expiration = null)
    {
        try
        {
            var xml = BuildToastXml(title, message);
            var script = BuildPowerShellScript(xml, expiration);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger?.Warning("Windows toast notification process could not be started.");
                return;
            }

            if (!process.WaitForExit(2000))
                return;

            if (process.ExitCode != 0)
                _logger?.Warning($"Windows toast notification process exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Windows toast notification unavailable: {ex.Message}");
        }
    }

    private static string BuildToastXml(string title, string message)
    {
        static string Escape(string value)
            => SecurityElement.Escape(value) ?? string.Empty;

        return $"<toast><visual><binding template='ToastGeneric'><text>{Escape(title)}</text><text>{Escape(message)}</text></binding></visual></toast>";
    }

    private static string BuildPowerShellScript(string xml, TimeSpan? expiration)
    {
        var escapedXml = xml.Replace("'", "''", StringComparison.Ordinal);
        const string appId = "SlurmPilot";
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null",
            "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument",
            $"$doc.LoadXml('{escapedXml}')",
            "$toast = [Windows.UI.Notifications.ToastNotification]::new($doc)",
        };

        if (expiration is { TotalSeconds: > 0 })
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"$toast.ExpirationTime = [DateTimeOffset]::Now.AddSeconds({Math.Ceiling(expiration.Value.TotalSeconds)})"));

        lines.Add($"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{appId}').Show($toast)");
        return string.Join("; ", lines);
    }
}
