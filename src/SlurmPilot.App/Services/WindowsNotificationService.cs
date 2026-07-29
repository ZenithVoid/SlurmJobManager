using System.Diagnostics;
using System.Security;
using SlurmPilot.Core.Interfaces;
using WF = System.Windows.Forms;

namespace SlurmPilot.App.Services;

/// <summary>Windows native toast notification implementation.</summary>
public sealed class WindowsNotificationService : INotificationService, IDisposable
{
    private readonly IAppLogger? _logger;
    private readonly object _balloonGate = new();
    private WF.NotifyIcon? _balloonIcon;
    private System.Threading.Timer? _balloonCleanupTimer;

    public WindowsNotificationService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public void Show(string title, string message, TimeSpan? expiration = null)
    {
        if (ShouldUseTrayBalloonFallback())
        {
            ShowTrayBalloon(title, message, expiration);
            return;
        }

        if (TryShowWinRtToast(title, message, expiration))
            return;

        ShowTrayBalloon(title, message, expiration);
    }

    public void Dispose()
    {
        lock (_balloonGate)
        {
            _balloonCleanupTimer?.Dispose();
            _balloonCleanupTimer = null;
            _balloonIcon?.Dispose();
            _balloonIcon = null;
        }
    }

    private bool TryShowWinRtToast(string title, string message, TimeSpan? expiration)
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
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger?.Warning("Windows toast notification process could not be started.");
                return false;
            }

            if (!process.WaitForExit(2000))
                return true;

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd().Trim();
                _logger?.Warning($"Windows toast notification process exited with code {process.ExitCode}. {stderr}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Windows toast notification unavailable: {ex.Message}");
            return false;
        }
    }

    private void ShowTrayBalloon(string title, string message, TimeSpan? expiration)
    {
        try
        {
            var timeoutMs = ResolveBalloonTimeoutMs(expiration);
            lock (_balloonGate)
            {
                _balloonCleanupTimer?.Dispose();
                _balloonCleanupTimer = null;
                _balloonIcon ??= CreateBalloonNotifyIcon();
                _balloonIcon.BalloonTipTitle = title;
                _balloonIcon.BalloonTipText = message;
                _balloonIcon.BalloonTipIcon = WF.ToolTipIcon.Info;
                _balloonIcon.Visible = true;
                _balloonIcon.ShowBalloonTip(timeoutMs);

                _balloonCleanupTimer = new System.Threading.Timer(
                    _ => HideBalloonIcon(),
                    null,
                    timeoutMs + 5000,
                    Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Windows tray notification unavailable: {ex.Message}");
        }
    }

    private void HideBalloonIcon()
    {
        lock (_balloonGate)
        {
            if (_balloonIcon != null)
                _balloonIcon.Visible = false;
        }
    }

    private static WF.NotifyIcon CreateBalloonNotifyIcon()
        => new()
        {
            Icon = LoadNotificationIcon(),
            Text = "SlurmPilot",
            Visible = false,
        };

    private static System.Drawing.Icon LoadNotificationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // Best effort: fall back to the stock application icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private static int ResolveBalloonTimeoutMs(TimeSpan? expiration)
    {
        if (!expiration.HasValue)
            return 10000;

        return (int)Math.Clamp(expiration.Value.TotalMilliseconds, 1000, 30000);
    }

    private static bool ShouldUseTrayBalloonFallback()
    {
        var version = Environment.OSVersion.Version;
        return version.Major == 10 && version.Build < 22000;
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
