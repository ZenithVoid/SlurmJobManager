using System.Reflection;
using System.Text;

namespace SlurmPilot.App.Services.CrashHandling;

/// <summary>
/// Builds human-readable crash reports from unhandled exceptions.
/// </summary>
internal static class CrashReportBuilder
{
    /// <summary>
    /// Builds a full diagnostic report string for the given exception.
    /// </summary>
    public static string BuildReport(Exception ex)
    {
        var sb = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"版本：{version}");
        sb.AppendLine($"线程：{Thread.CurrentThread.ManagedThreadId} ({Thread.CurrentThread.Name ?? "unnamed"})");
        sb.AppendLine();
        AppendException(sb, ex, 0);

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the most useful location string from an exception's stack trace.
    /// Prefers the first frame in the SlurmPilot namespace; falls back to first frame.
    /// </summary>
    public static string ExtractLocation(Exception ex)
    {
        if (string.IsNullOrEmpty(ex.StackTrace))
            return "未知位置";

        var frames = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Prefer the first frame from our own namespace for a meaningful location
        var ownFrame = frames.FirstOrDefault(f =>
            f.TrimStart().StartsWith("at SlurmPilot.", StringComparison.Ordinal));

        if (ownFrame != null)
            return ownFrame.Trim();

        return frames.FirstOrDefault()?.Trim() ?? "未知位置";
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);

        sb.AppendLine($"{indent}异常类型：{ex.GetType().FullName}");
        sb.AppendLine($"{indent}消息：{ex.Message}");

        if (!string.IsNullOrEmpty(ex.Source))
            sb.AppendLine($"{indent}来源：{ex.Source}");

        if (ex.TargetSite != null)
            sb.AppendLine($"{indent}方法：{ex.TargetSite}");

        sb.AppendLine($"{indent}堆栈：");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"{indent}  {line.TrimEnd()}");
        }

        if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}--- 内部异常 ---");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }
}
