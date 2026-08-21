using BitMiracle.LibTiff.Classic;
using System.IO;

namespace SlurmPilot.VolumeTiffPlugin;

internal sealed record VolumeData(int Width, int Height, int Depth, int BitsPerSample,
    ushort[] Voxels, ushort Minimum, ushort Maximum);

internal sealed record TiffVolumeInfo(
    string Path,
    long FileSize,
    int Width,
    int Height,
    int Depth,
    int BitsPerSample,
    int SamplesPerPixel,
    string Compression,
    string Photometric,
    bool IsTiled,
    double? XResolution,
    double? YResolution,
    string ResolutionUnit);

internal static class TiffVolumeReader
{
    private const long MaximumVoxelBytes = 1_500_000_000;

    public static VolumeData Read(string path, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        progress?.Report(0.02);
        using var image = Tiff.Open(path, "r") ?? throw new InvalidDataException("无法打开 TIFF 文件。");
        var directories = InspectDirectories(image, cancellationToken);
        progress?.Report(0.08);
        var first = directories[0];
        var voxelCount = checked((long)first.Width * first.Height * directories.Count);
        if (voxelCount * sizeof(ushort) > MaximumVoxelBytes || voxelCount > int.MaxValue)
            throw new InvalidDataException("体数据过大；解码后的体素缓存上限为 1.5 GB。");

        var voxels = new ushort[(int)voxelCount];
        ushort minimum = ushort.MaxValue;
        ushort maximum = ushort.MinValue;
        for (short z = 0; z < directories.Count; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!image.SetDirectory(z)) throw new InvalidDataException($"无法切换到 TIFF 第 {z + 1} 页。");
            var info = directories[z];
            if (info.Width != first.Width || info.Height != first.Height || info.Bits != first.Bits)
                throw new InvalidDataException("TIFF 各页的尺寸和位深必须一致。");
            if (image.IsTiled())
                ReadTiledPage(image, info, z, voxels, ref minimum, ref maximum, cancellationToken);
            else
                ReadScanlinePage(image, info, z, voxels, ref minimum, ref maximum, cancellationToken);
            progress?.Report(0.08 + 0.82 * (z + 1d) / directories.Count);
        }

        progress?.Report(0.90);
        return new VolumeData(first.Width, first.Height, directories.Count, first.Bits, voxels, minimum, maximum);
    }

    public static TiffVolumeInfo Inspect(string path, CancellationToken cancellationToken = default)
    {
        using var image = Tiff.Open(path, "r") ?? throw new InvalidDataException("无法打开 TIFF 文件。");
        var directories = InspectDirectories(image, cancellationToken);
        if (!image.SetDirectory(0)) throw new InvalidDataException("无法读取 TIFF 首页。 ");
        var first = directories[0];
        var samples = OptionalInt(image, TiffTag.SAMPLESPERPIXEL, 1);
        var compression = (Compression)OptionalInt(image, TiffTag.COMPRESSION, (int)Compression.NONE);
        var photometric = (Photometric)OptionalInt(image, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
        var resolutionUnit = (ResUnit)OptionalInt(image, TiffTag.RESOLUTIONUNIT, (int)ResUnit.NONE);
        return new TiffVolumeInfo(
            path,
            new FileInfo(path).Length,
            first.Width,
            first.Height,
            directories.Count,
            first.Bits,
            samples,
            compression.ToString(),
            photometric.ToString(),
            image.IsTiled(),
            OptionalDouble(image, TiffTag.XRESOLUTION),
            OptionalDouble(image, TiffTag.YRESOLUTION),
            resolutionUnit.ToString());
    }

    private static List<PageInfo> InspectDirectories(Tiff image, CancellationToken cancellationToken)
    {
        var result = new List<PageInfo>();
        image.SetDirectory(0);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var width = RequiredInt(image, TiffTag.IMAGEWIDTH);
            var height = RequiredInt(image, TiffTag.IMAGELENGTH);
            var bits = OptionalInt(image, TiffTag.BITSPERSAMPLE, 1);
            var samples = OptionalInt(image, TiffTag.SAMPLESPERPIXEL, 1);
            var photometric = (Photometric)OptionalInt(image, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
            var format = (SampleFormat)OptionalInt(image, TiffTag.SAMPLEFORMAT, (int)SampleFormat.UINT);
            if (samples != 1) throw new InvalidDataException($"仅支持单通道 TIFF；检测到 {samples} 个通道。");
            if (bits is not (8 or 16)) throw new InvalidDataException($"仅支持 8 或 16 位 TIFF；检测到 {bits} 位。");
            if (format is not (SampleFormat.UINT or SampleFormat.VOID))
                throw new InvalidDataException($"仅支持无符号整数灰度 TIFF；SampleFormat={format}。");
            if (photometric is not (Photometric.MINISBLACK or Photometric.MINISWHITE))
                throw new InvalidDataException($"仅支持灰度 TIFF；Photometric={photometric}。");
            result.Add(new PageInfo(width, height, bits, photometric == Photometric.MINISWHITE));
            if (result.Count > short.MaxValue)
                throw new InvalidDataException($"TIFF 页数不能超过 {short.MaxValue}。");
        } while (image.ReadDirectory());
        if (result.Count == 0) throw new InvalidDataException("TIFF 中没有图像页。");
        return result;
    }

    private static void ReadScanlinePage(Tiff image, PageInfo info, int z, ushort[] target,
        ref ushort minimum, ref ushort maximum, CancellationToken token)
    {
        var row = new byte[image.ScanlineSize()];
        for (var y = 0; y < info.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            if (!image.ReadScanline(row, y)) throw new InvalidDataException($"无法读取第 {z + 1} 页第 {y + 1} 行。");
            CopyPixels(row, 0, target, ((z * info.Height) + y) * info.Width,
                info.Width, info.Bits, info.Invert, ref minimum, ref maximum);
        }
    }

    private static void ReadTiledPage(Tiff image, PageInfo info, int z, ushort[] target,
        ref ushort minimum, ref ushort maximum, CancellationToken token)
    {
        var tileWidth = RequiredInt(image, TiffTag.TILEWIDTH);
        var tileHeight = RequiredInt(image, TiffTag.TILELENGTH);
        var bytesPerPixel = info.Bits / 8;
        var tile = new byte[image.TileSize()];
        for (var tileY = 0; tileY < info.Height; tileY += tileHeight)
        for (var tileX = 0; tileX < info.Width; tileX += tileWidth)
        {
            token.ThrowIfCancellationRequested();
            var tileIndex = image.ComputeTile(tileX, tileY, 0, 0);
            if (image.ReadEncodedTile(tileIndex, tile, 0, tile.Length) < 0)
                throw new InvalidDataException($"无法读取第 {z + 1} 页瓦片 ({tileX},{tileY})。");
            var copyWidth = Math.Min(tileWidth, info.Width - tileX);
            var copyHeight = Math.Min(tileHeight, info.Height - tileY);
            for (var row = 0; row < copyHeight; row++)
                CopyPixels(tile, row * tileWidth * bytesPerPixel, target,
                    ((z * info.Height + tileY + row) * info.Width) + tileX,
                    copyWidth, info.Bits, info.Invert, ref minimum, ref maximum);
        }
    }

    private static void CopyPixels(byte[] source, int sourceOffset, ushort[] target, int targetOffset,
        int count, int bits, bool invert, ref ushort minimum, ref ushort maximum)
    {
        for (var x = 0; x < count; x++)
        {
            var value = bits == 8
                ? (ushort)(source[sourceOffset + x] * 257)
                : BitConverter.ToUInt16(source, sourceOffset + x * 2);
            if (invert) value = (ushort)(ushort.MaxValue - value);
            target[targetOffset + x] = value;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }
    }

    private static int RequiredInt(Tiff image, TiffTag tag)
        => image.GetField(tag)?[0].ToInt() ?? throw new InvalidDataException($"TIFF 缺少必要标签 {tag}。");
    private static int OptionalInt(Tiff image, TiffTag tag, int fallback)
        => image.GetField(tag)?[0].ToInt() ?? fallback;
    private static double? OptionalDouble(Tiff image, TiffTag tag)
        => image.GetField(tag)?[0].ToDouble();
    private sealed record PageInfo(int Width, int Height, int Bits, bool Invert);
}
