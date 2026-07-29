using System.Text;

namespace SlurmPilot.App.Services;

public sealed class TextEncodingDetectionResult
{
    public required Encoding Encoding { get; init; }
    public required string DisplayName { get; init; }
    public bool HasBom { get; init; }
    public bool IsReliable { get; init; }
    public bool IsBinaryLike { get; init; }
    public string? WarningMessage { get; init; }
}

public static class TextEncodingDetector
{
    static TextEncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static TextEncodingDetectionResult Detect(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return new TextEncodingDetectionResult
            {
                Encoding = new UTF8Encoding(false),
                DisplayName = "UTF-8",
                HasBom = false,
                IsReliable = true,
                IsBinaryLike = false,
            };
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Build(Encoding.UTF8, "UTF-8 BOM", hasBom: true, reliable: true, binaryLike: false);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Build(Encoding.Unicode, "UTF-16 LE", hasBom: true, reliable: true, binaryLike: false);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Build(Encoding.BigEndianUnicode, "UTF-16 BE", hasBom: true, reliable: true, binaryLike: false);

        var sampleLength = Math.Min(bytes.Length, 4096);
        var nullCount = 0;
        for (var i = 0; i < sampleLength; i++)
        {
            if (bytes[i] == 0) nullCount++;
        }

        if (nullCount > sampleLength / 16)
        {
            return new TextEncodingDetectionResult
            {
                Encoding = new UTF8Encoding(false),
                DisplayName = "UTF-8",
                HasBom = false,
                IsReliable = false,
                IsBinaryLike = true,
                WarningMessage = "检测到较多二进制特征，已阻止按文本打开。",
            };
        }

        if (CanDecodeStrict(bytes, new UTF8Encoding(false, true)))
            return Build(new UTF8Encoding(false), "UTF-8", hasBom: false, reliable: true, binaryLike: false);

        if (LooksLikeUtf16WithoutBom(bytes))
        {
            if (LooksLikeUtf16Le(bytes))
                return Build(new UnicodeEncoding(false, false, true), "UTF-16 LE", hasBom: false, reliable: false, binaryLike: false);
            if (LooksLikeUtf16Be(bytes))
                return Build(new UnicodeEncoding(true, false, true), "UTF-16 BE", hasBom: false, reliable: false, binaryLike: false);
        }

        var gb18030 = Encoding.GetEncoding(
            "GB18030",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        if (CanDecodeStrict(bytes, gb18030))
            return Build(Encoding.GetEncoding("GB18030"), "GB18030", hasBom: false, reliable: false, binaryLike: false);

        return new TextEncodingDetectionResult
        {
            Encoding = new UTF8Encoding(false),
            DisplayName = "UTF-8(推测)",
            HasBom = false,
            IsReliable = false,
            IsBinaryLike = false,
            WarningMessage = "编码无法可靠识别，已按 UTF-8 打开；保存前请确认编码。",
        };
    }

    public static byte[] Encode(string content, TextEncodingDetectionResult detection)
    {
        var encoding = detection.Encoding;
        var body = encoding.GetBytes(content);
        if (!detection.HasBom) return body;

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0) return body;

        var buffer = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, buffer, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, buffer, preamble.Length, body.Length);
        return buffer;
    }

    private static TextEncodingDetectionResult Build(Encoding encoding, string displayName, bool hasBom, bool reliable, bool binaryLike) =>
        new()
        {
            Encoding = encoding,
            DisplayName = displayName,
            HasBom = hasBom,
            IsReliable = reliable,
            IsBinaryLike = binaryLike,
        };

    private static bool CanDecodeStrict(byte[] bytes, Encoding encoding)
    {
        try
        {
            _ = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool LooksLikeUtf16WithoutBom(byte[] bytes) =>
        LooksLikeUtf16Le(bytes) || LooksLikeUtf16Be(bytes);

    private static bool LooksLikeUtf16Le(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        var nullsOnOdd = 0;
        var pairs = Math.Min(bytes.Length / 2, 1024);
        for (var i = 0; i < pairs; i++)
        {
            if (bytes[(i * 2) + 1] == 0) nullsOnOdd++;
        }
        return nullsOnOdd > pairs / 2;
    }

    private static bool LooksLikeUtf16Be(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        var nullsOnEven = 0;
        var pairs = Math.Min(bytes.Length / 2, 1024);
        for (var i = 0; i < pairs; i++)
        {
            if (bytes[i * 2] == 0) nullsOnEven++;
        }
        return nullsOnEven > pairs / 2;
    }
}
