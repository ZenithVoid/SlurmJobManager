using SlurmPilot.Plugin.Abstractions;
using SlurmPilot.VolumeTiffPlugin;

if (FfmpegVolumeReader.CombineBytes(0x12, 0x34) != 0x1234)
    throw new InvalidDataException("High/low MP4 byte composition failed.");
Console.WriteLine("PASS MP4 high/low composition: 0x12 + 0x34 = 0x1234");

var auto8Voxels = new ushort[] { 0, 100 * 257, 100 * 257, 200 * 257, 200 * 257, 65535 };
var auto8 = VolumeViewerControl.CalculateAutoRange(new VolumeData(3, 2, 1, 8, auto8Voxels, 0, 65535));
if (auto8 != (100, 200))
    throw new InvalidDataException($"Unexpected 8-bit auto window: {auto8}.");
Console.WriteLine("PASS 8-bit auto window: 100..200");

var auto16Voxels = new ushort[] { 0, 1000, 1000, 5000, 5000, 65535 };
var auto16 = VolumeViewerControl.CalculateAutoRange(new VolumeData(3, 2, 1, 16, auto16Voxels, 0, 65535));
if (auto16 != (992, 5007))
    throw new InvalidDataException($"Unexpected 16-bit auto window: {auto16}.");
Console.WriteLine("PASS 16-bit auto window: 992..5007");

if (VolumeViewerControl.ToDisplayValue(100 * 257, 8) != 100 ||
    VolumeViewerControl.ToDisplayValue(5000, 16) != 5000)
    throw new InvalidDataException("Measured volume range was not converted to display values correctly.");
Console.WriteLine("PASS measured image range conversion for 8-bit and 16-bit data");

var sizingVolume = new VolumeData(100, 50, 10, 16, [], 0, 0);
if (VolumeViewerControl.EstimateDisplayBytes(sizingVolume) != 100_000)
    throw new InvalidDataException("Unexpected display-buffer estimate.");
var landscapeDistance = VolumeViewerControl.CalculateFitDistance(100, 100, 100, 16f / 9f);
var portraitDistance = VolumeViewerControl.CalculateFitDistance(100, 100, 100, 0.5f);
if (landscapeDistance < 2.6f || landscapeDistance > 2.8f || portraitDistance <= landscapeDistance)
    throw new InvalidDataException($"Unexpected camera fit distances: {landscapeDistance}, {portraitDistance}.");
Console.WriteLine($"PASS centered full-view sizing: 100000 bytes, camera {landscapeDistance:0.00}/{portraitDistance:0.00}");

var forwardSlices = VolumeViewerControl.BuildViewAlignedSliceGeometry(OpenTK.Mathematics.Vector3.UnitZ, 4);
var reverseSlices = VolumeViewerControl.BuildViewAlignedSliceGeometry(-OpenTK.Mathematics.Vector3.UnitZ, 4);
if (forwardSlices.Length != 4 * 6 * 6 ||
    Math.Abs(forwardSlices[2] - (-0.375f)) > 0.0001f ||
    Math.Abs(forwardSlices[5] - 0.125f) > 0.0001f ||
    Math.Abs(reverseSlices[2] - 0.375f) > 0.0001f ||
    Math.Abs(reverseSlices[5] - 0.875f) > 0.0001f)
    throw new InvalidDataException("Volume slices were not generated in back-to-front order.");
var diagonalSlices = VolumeViewerControl.BuildViewAlignedSliceGeometry(
    OpenTK.Mathematics.Vector3.Normalize(new(1, 1, 1)), 32);
if (diagonalSlices.Length == 0 || diagonalSlices.Any(value => value < -0.5001f || value > 1.0001f))
    throw new InvalidDataException("View-aligned volume slices do not cover the unit volume.");
Console.WriteLine("PASS continuous view-aligned volume slice geometry");

var continuousRotation = OpenTK.Mathematics.Matrix4.Identity;
for (var index = 0; index < 1000; index++)
    continuousRotation = VolumeViewerControl.ApplyDragRotation(continuousRotation, 10f, 6f);
var row0 = continuousRotation.Row0.Xyz;
var row1 = continuousRotation.Row1.Xyz;
var row2 = continuousRotation.Row2.Xyz;
if (Math.Abs(row0.Length - 1f) > 0.001f || Math.Abs(row1.Length - 1f) > 0.001f ||
    Math.Abs(row2.Length - 1f) > 0.001f || Math.Abs(OpenTK.Mathematics.Vector3.Dot(row0, row1)) > 0.001f)
    throw new InvalidDataException("Continuous drag rotation accumulated scale or shear.");
Console.WriteLine("PASS unlimited normalized drag rotation");

if (!VolumeViewerControl.VolumeVertexShader.Contains(
        "gl_Position = vec4(aPosition, 1.0) * uMvp;", StringComparison.Ordinal))
    throw new InvalidDataException("OpenTK row-major vertex transform order regressed.");
Console.WriteLine("PASS OpenTK row-major vertex transform order");

var windowDomain = VolumeWindowRangeControl.CreateDomain(0, 97);
if (windowDomain != (-97, 194))
    throw new InvalidDataException($"Unexpected volume window domain: {windowDomain}.");
Console.WriteLine("PASS signed precision window domain: -97..194");

if (VolumeViewerControl.CalculateRenderSliceCount(443, interacting: true) != 110 ||
    VolumeViewerControl.CalculateRenderSliceCount(443, interacting: false) != 443)
    throw new InvalidDataException("Interactive volume quality scaling regressed.");
Console.WriteLine("PASS interactive volume sampling: 443 -> 110 -> 443");

var sparseOpacity = VolumeViewerControl.CalculateVoxelOpacity(1f, 0.55f);
var faintOpacity = VolumeViewerControl.CalculateVoxelOpacity(0.1f, 0.55f);
if (sparseOpacity < 0.5f || faintOpacity < 0.05f ||
    VolumeViewerControl.CalculateVoxelOpacity(1f, 0f) != 0f)
    throw new InvalidDataException($"Sparse-volume opacity is not visible enough: {faintOpacity}/{sparseOpacity}.");
Console.WriteLine($"PASS sparse-volume opacity transfer: {faintOpacity:0.000}..{sparseOpacity:0.000}");

var tiffPaths = args.Where(path => Path.GetExtension(path) is ".tif" or ".tiff").ToArray();
foreach (var path in tiffPaths)
{
    var volume = TiffVolumeReader.Read(path, CancellationToken.None);
    if (volume.Width <= 0 || volume.Height <= 0 || volume.Depth <= 1)
        throw new InvalidDataException($"Invalid volume dimensions for {path}.");
    if (volume.BitsPerSample is not (8 or 16))
        throw new InvalidDataException($"Unexpected bit depth for {path}.");
    if (volume.Maximum <= volume.Minimum)
        throw new InvalidDataException($"Expected a non-constant volume for {path}.");
    var activeThreshold = (ushort)Math.Max(volume.Minimum + 1, volume.Minimum + (volume.Maximum - volume.Minimum) * 0.18);
    var activeX = new bool[volume.Width];
    var activeY = new bool[volume.Height];
    var activeZ = new bool[volume.Depth];
    long activeCount = 0;
    double sumX = 0, sumY = 0, sumZ = 0;
    double sumXX = 0, sumYY = 0, sumZZ = 0, sumXY = 0, sumXZ = 0, sumYZ = 0;
    for (var z = 0; z < volume.Depth; z++)
    for (var y = 0; y < volume.Height; y++)
    for (var x = 0; x < volume.Width; x++)
    {
        if (volume.Voxels[((z * volume.Height) + y) * volume.Width + x] < activeThreshold) continue;
        activeX[x] = true;
        activeY[y] = true;
        activeZ[z] = true;
        activeCount++;
        sumX += x; sumY += y; sumZ += z;
        sumXX += x * (double)x; sumYY += y * (double)y; sumZZ += z * (double)z;
        sumXY += x * (double)y; sumXZ += x * (double)z; sumYZ += y * (double)z;
    }
    var principalThickness = PrincipalThickness(activeCount, sumX, sumY, sumZ,
        sumXX, sumYY, sumZZ, sumXY, sumXZ, sumYZ);
    var occupied = $"occupied X={activeX.Count(value => value)}/{volume.Width}, " +
                   $"Y={activeY.Count(value => value)}/{volume.Height}, Z={activeZ.Count(value => value)}/{volume.Depth}, " +
                   $"principal thickness={principalThickness.Min:0.0}/{principalThickness.Middle:0.0}/{principalThickness.Max:0.0}";
    Console.WriteLine($"PASS {Path.GetFileName(path)}: {volume.Width}x{volume.Height}x{volume.Depth}, " +
                      $"{volume.BitsPerSample}-bit, range {volume.Minimum}..{volume.Maximum}, {occupied}");
}

static (double Min, double Middle, double Max) PrincipalThickness(long count,
    double sx, double sy, double sz, double sxx, double syy, double szz,
    double sxy, double sxz, double syz)
{
    if (count <= 1) return (0, 0, 0);
    var mx = sx / count; var my = sy / count; var mz = sz / count;
    var a = sxx / count - mx * mx;
    var d = syy / count - my * my;
    var f = szz / count - mz * mz;
    var b = sxy / count - mx * my;
    var c = sxz / count - mx * mz;
    var e = syz / count - my * mz;
    var q = (a + d + f) / 3d;
    var p = Math.Sqrt(((a - q) * (a - q) + (d - q) * (d - q) + (f - q) * (f - q) +
                       2d * (b * b + c * c + e * e)) / 6d);
    if (p < 1e-12)
    {
        var same = Math.Sqrt(Math.Max(0, q));
        return (same, same, same);
    }
    var ba = (a - q) / p; var bd = (d - q) / p; var bf = (f - q) / p;
    var bb = b / p; var bc = c / p; var be = e / p;
    var determinant = ba * (bd * bf - be * be) - bb * (bb * bf - be * bc) + bc * (bb * be - bd * bc);
    var angle = Math.Acos(Math.Clamp(determinant / 2d, -1d, 1d)) / 3d;
    var eigenvalues = new[]
    {
        q + 2d * p * Math.Cos(angle),
        q + 2d * p * Math.Cos(angle + 2d * Math.PI / 3d),
        q + 2d * p * Math.Cos(angle + 4d * Math.PI / 3d)
    };
    Array.Sort(eigenvalues);
    return (Math.Sqrt(Math.Max(0, eigenvalues[0])),
        Math.Sqrt(Math.Max(0, eigenvalues[1])), Math.Sqrt(Math.Max(0, eigenvalues[2])));
}

var mp4Paths = args.Where(path => Path.GetExtension(path) == ".mp4").ToArray();
if (mp4Paths.Length > 0)
{
    var high = mp4Paths.SingleOrDefault(path => Path.GetFileNameWithoutExtension(path)
        .EndsWith("_high", StringComparison.OrdinalIgnoreCase));
    var low = mp4Paths.SingleOrDefault(path => Path.GetFileNameWithoutExtension(path)
        .EndsWith("_low", StringComparison.OrdinalIgnoreCase));
    if (high == null || low == null)
        throw new InvalidDataException("Expected one _high.mp4 and one _low.mp4 sample.");

    var context = new TestPluginContext(Path.Combine(Directory.GetCurrentDirectory(), "plugin"));
    try
    {
        var volume = await FfmpegVolumeReader.ReadPairAsync(high, low, context, CancellationToken.None);
        if (volume.Width <= 0 || volume.Height <= 0 || volume.Depth <= 1)
            throw new InvalidDataException("Invalid MP4 volume dimensions.");
        if (volume.BitsPerSample != 16)
            throw new InvalidDataException($"Unexpected MP4 bit depth: {volume.BitsPerSample}.");
        if (volume.Maximum <= volume.Minimum)
            throw new InvalidDataException("Expected a non-constant MP4 volume.");
        Console.WriteLine($"PASS MP4 pair: {volume.Width}x{volume.Height}x{volume.Depth}, " +
                          $"{volume.BitsPerSample}-bit, range {volume.Minimum}..{volume.Maximum}");
    }
    catch (FileNotFoundException ex) when (ex.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                                          || ex.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"SKIP MP4 pair: {ex.Message}");
    }
}

internal sealed class TestPluginContext(string pluginDirectory) : IPluginContext
{
    public string PluginDirectory { get; } = pluginDirectory;
    public Version HostVersion { get; } = new(10, 0);
    public void Log(PluginLogLevel level, string message, Exception? exception = null) { }
    public void ShowInformation(string title, string message, string? details = null) { }
    public void ShowWarning(string title, string message, string? details = null) { }
}
