using SlurmPilot.Plugin.Abstractions;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SlurmPilot.VolumeTiffPlugin;

internal sealed record Mp4VolumeInfo(
    string Path,
    long FileSize,
    int Width,
    int Height,
    long? FrameCount,
    string CodecName,
    string CodecLongName,
    string Profile,
    string PixelFormat,
    string BitsPerRawSample,
    string FrameRate,
    string Duration,
    string BitRate,
    string Container,
    string ProbeExecutable);

internal static class FfmpegVolumeReader
{
    private const long MaximumVoxelBytes = 1_500_000_000;

    public static async Task<(Mp4VolumeInfo High, Mp4VolumeInfo Low)> InspectPairAsync(
        string highPath, string lowPath, IPluginContext context, CancellationToken token)
    {
        var probe = FindTool(context.PluginDirectory, "ffprobe.exe");
        var highTask = InspectAsync(highPath, probe, token);
        var lowTask = InspectAsync(lowPath, probe, token);
        await Task.WhenAll(highTask, lowTask);
        var high = await highTask;
        var low = await lowTask;
        ValidatePair(high, low, requireFrameCount: false);
        return (high, low);
    }

    public static async Task<VolumeData> ReadPairAsync(
        string highPath, string lowPath, IPluginContext context, CancellationToken token)
    {
        var (high, low) = await InspectPairAsync(highPath, lowPath, context, token);
        ValidatePair(high, low, requireFrameCount: false);
        var ffmpeg = FindTool(context.PluginDirectory, "ffmpeg.exe");
        var tempRoot = Path.Combine(Path.GetTempPath(), "SlurmPilot.VolumeTiffPlugin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var highRaw = Path.Combine(tempRoot, "high.gray8");
        var lowRaw = Path.Combine(tempRoot, "low.gray8");
        try
        {
            await DecodeGray8Async(ffmpeg, highPath, highRaw, token);
            await DecodeGray8Async(ffmpeg, lowPath, lowRaw, token);
            var highLength = new FileInfo(highRaw).Length;
            var lowLength = new FileInfo(lowRaw).Length;
            var frameBytes = checked((long)high.Width * high.Height);
            if (highLength != lowLength)
                throw new InvalidDataException($"高位与低位视频解码后的数据长度不一致：{highLength} / {lowLength} 字节。");
            if (highLength == 0 || highLength % frameBytes != 0)
                throw new InvalidDataException("MP4 解码结果无法按视频宽高组成完整切片。");
            var depth = highLength / frameBytes;
            if (depth > int.MaxValue)
                throw new InvalidDataException("MP4 切片数量过大。");
            if (highLength * sizeof(ushort) > MaximumVoxelBytes || highLength > int.MaxValue)
                throw new InvalidDataException("体数据过大；合并后的体素缓存上限为 1.5 GB。");
            if (high.FrameCount is > 0 && high.FrameCount != depth)
                throw new InvalidDataException($"高位 MP4 声明 {high.FrameCount} 帧，但实际解码出 {depth} 帧。");
            if (low.FrameCount is > 0 && low.FrameCount != depth)
                throw new InvalidDataException($"低位 MP4 声明 {low.FrameCount} 帧，但实际解码出 {depth} 帧。");

            return await CombineAsync(highRaw, lowRaw, high.Width, high.Height, (int)depth, token);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (Exception ex) { context.Log(PluginLogLevel.Warning, $"无法删除 MP4 解码临时目录：{tempRoot}", ex); }
        }
    }

    public static string FormatInfo(Mp4VolumeInfo info, string label)
        => $"{label}\n" +
           $"文件：{info.Path}\n" +
           $"大小：{FormatBytes(info.FileSize)}\n" +
           $"画面：{info.Width} × {info.Height}\n" +
           $"帧数：{info.FrameCount?.ToString(CultureInfo.InvariantCulture) ?? "未知"}\n" +
           $"编码：{info.CodecLongName} ({info.CodecName})\n" +
           $"Profile：{Fallback(info.Profile)}\n" +
           $"像素格式：{Fallback(info.PixelFormat)}\n" +
           $"原始位深：{Fallback(info.BitsPerRawSample)}\n" +
           $"帧率：{Fallback(info.FrameRate)}\n" +
           $"时长：{Fallback(info.Duration)} 秒\n" +
           $"码率：{Fallback(info.BitRate)} bit/s\n" +
           $"容器：{Fallback(info.Container)}";

    private static async Task<Mp4VolumeInfo> InspectAsync(string path, string probe, CancellationToken token)
    {
        var arguments = new[]
        {
            "-v", "error", "-select_streams", "v:0", "-count_frames",
            "-show_entries",
            "stream=codec_name,codec_long_name,profile,width,height,pix_fmt,bits_per_raw_sample,r_frame_rate,avg_frame_rate,nb_frames,nb_read_frames,duration,bit_rate:format=format_name,format_long_name,duration,size,bit_rate",
            "-of", "json", path
        };
        var json = await RunCaptureAsync(probe, arguments, token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidDataException($"MP4 中没有视频流：{path}");
        var stream = streams[0];
        root.TryGetProperty("format", out var format);
        var width = GetInt(stream, "width");
        var height = GetInt(stream, "height");
        var frameCount = GetLong(stream, "nb_read_frames") ?? GetLong(stream, "nb_frames");
        return new Mp4VolumeInfo(
            path,
            new FileInfo(path).Length,
            width,
            height,
            frameCount,
            GetText(stream, "codec_name"),
            GetText(stream, "codec_long_name"),
            GetText(stream, "profile"),
            GetText(stream, "pix_fmt"),
            GetText(stream, "bits_per_raw_sample"),
            FirstValue(GetText(stream, "avg_frame_rate"), GetText(stream, "r_frame_rate")),
            FirstValue(GetText(stream, "duration"), GetText(format, "duration")),
            FirstValue(GetText(stream, "bit_rate"), GetText(format, "bit_rate")),
            FirstValue(GetText(format, "format_long_name"), GetText(format, "format_name")),
            probe);
    }

    private static void ValidatePair(Mp4VolumeInfo high, Mp4VolumeInfo low, bool requireFrameCount)
    {
        if (high.Width <= 0 || high.Height <= 0 || low.Width <= 0 || low.Height <= 0)
            throw new InvalidDataException("无法从 MP4 读取有效的画面尺寸。");
        if (high.Width != low.Width || high.Height != low.Height)
            throw new InvalidDataException($"高低位 MP4 尺寸不一致：{high.Width}×{high.Height} / {low.Width}×{low.Height}。");
        if (high.FrameCount.HasValue && low.FrameCount.HasValue && high.FrameCount != low.FrameCount)
            throw new InvalidDataException($"高低位 MP4 帧数不一致：{high.FrameCount} / {low.FrameCount}。");
        if (requireFrameCount && (!high.FrameCount.HasValue || !low.FrameCount.HasValue))
            throw new InvalidDataException("无法取得高低位 MP4 的准确帧数。");
    }

    private static async Task DecodeGray8Async(string ffmpeg, string input, string output, CancellationToken token)
    {
        await RunCaptureAsync(ffmpeg,
            ["-v", "error", "-nostdin", "-i", input, "-map", "0:v:0", "-vsync", "0",
             "-f", "rawvideo", "-pix_fmt", "gray", "-y", output], token);
    }

    private static async Task<VolumeData> CombineAsync(
        string highPath, string lowPath, int width, int height, int depth, CancellationToken token)
    {
        var voxelCount = checked(width * height * depth);
        var voxels = new ushort[voxelCount];
        ushort minimum = ushort.MaxValue;
        ushort maximum = ushort.MinValue;
        await using var highStream = new FileStream(highPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var lowStream = new FileStream(lowPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var highBuffer = new byte[1024 * 1024];
        var lowBuffer = new byte[highBuffer.Length];
        var offset = 0;
        while (offset < voxels.Length)
        {
            token.ThrowIfCancellationRequested();
            var wanted = Math.Min(highBuffer.Length, voxels.Length - offset);
            await ReadExactlyAsync(highStream, highBuffer, wanted, token);
            await ReadExactlyAsync(lowStream, lowBuffer, wanted, token);
            for (var i = 0; i < wanted; i++)
            {
                var value = CombineBytes(highBuffer[i], lowBuffer[i]);
                voxels[offset + i] = value;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
            }
            offset += wanted;
        }
        return new VolumeData(width, height, depth, 16, voxels, minimum, maximum);
    }

    internal static ushort CombineBytes(byte high, byte low) => (ushort)((high << 8) | low);

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            if (read == 0) throw new EndOfStreamException("MP4 灰度流意外结束。");
            offset += read;
        }
    }

    private static async Task<string> RunCaptureAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidDataException($"{Path.GetFileName(executable)} 执行失败（{process.ExitCode}）：{stderr.Trim()}");
        return stdout;
    }

    private static string FindTool(string pluginDirectory, string executableName)
    {
        var candidates = new[]
        {
            Path.Combine(pluginDirectory, executableName),
            Path.Combine(pluginDirectory, "ffmpeg", executableName),
            Path.Combine(pluginDirectory, "ffmpeg", "bin", executableName),
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", executableName)
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(folder, executableName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"未找到 {executableName}。请将 ffmpeg.exe 和 ffprobe.exe 放入 plugin\\ffmpeg\\bin，或加入系统 PATH。");
    }

    private static int GetInt(JsonElement element, string name)
        => int.TryParse(GetText(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static long? GetLong(JsonElement element, string name)
        => long.TryParse(GetText(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string GetText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }
    private static string FirstValue(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first;
    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) || value == "N/A" ? "未知" : value;
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]} ({bytes:N0} 字节)";
    }
}
