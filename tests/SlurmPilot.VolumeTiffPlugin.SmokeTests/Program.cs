using SlurmPilot.VolumeTiffPlugin;

if (FfmpegVolumeReader.CombineBytes(0x12, 0x34) != 0x1234)
    throw new InvalidDataException("High/low MP4 byte composition failed.");
Console.WriteLine("PASS MP4 high/low composition: 0x12 + 0x34 = 0x1234");

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
