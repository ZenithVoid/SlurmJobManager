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

foreach (var path in args)
{
    var volume = TiffVolumeReader.Read(path, CancellationToken.None);
    if (volume.Width <= 0 || volume.Height <= 0 || volume.Depth <= 1)
        throw new InvalidDataException($"Invalid volume dimensions for {path}.");
    if (volume.BitsPerSample is not (8 or 16))
        throw new InvalidDataException($"Unexpected bit depth for {path}.");
    if (volume.Maximum <= volume.Minimum)
        throw new InvalidDataException($"Expected a non-constant volume for {path}.");
    Console.WriteLine($"PASS {Path.GetFileName(path)}: {volume.Width}x{volume.Height}x{volume.Depth}, " +
                      $"{volume.BitsPerSample}-bit, range {volume.Minimum}..{volume.Maximum}");
}
