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

    public void Show(string title, string message)
    {
        try
        {
            var xml = BuildToastXml(title, message);
            var script = BuildPowerShellScript(xml);
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

    private static string BuildPowerShellScript(string xml)
    {
        var escapedXml = xml.Replace("'", "''", StringComparison.Ordinal);
        const string appId = "SlurmPilot";
        return string.Join("; ", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null",
            "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument",
            $"$doc.LoadXml('{escapedXml}')",
            "$toast = [Windows.UI.Notifications.ToastNotification]::new($doc)",
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{appId}').Show($toast)"
        });
    }
}
