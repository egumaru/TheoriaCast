using System.Buffers.Binary;
using System.Text.Json;
using OpenCvSharp;

namespace TheoriaCast.Visual.Api;

internal static partial class VisualImages
{
    private const int MaxPixels = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static Mat Decode(byte[] bytes, bool normalizeJpeg = true)
    {
        if (bytes.Length is 0 or > VisualProtocol.MaxImageBytes)
            throw new InvalidDataException("画像の上限は 16 MiB です。");
        var (width, height) = Dimensions(bytes);
        if (width is 0 or > 8192 || height is 0 or > 8192 || (long)width * height > MaxPixels)
            throw new InvalidDataException("画像の上限は各辺 8192 ピクセル、全体で 419 万画素です。");
        bool orient = normalizeJpeg && !IsPng(bytes);
        Mat image;
        // JPEG can honor EXIF without forcing grayscale or color. Keep PNG alpha and depth unchanged.
        try { image = Cv2.ImDecode(bytes, orient ? ImreadModes.AnyColor : ImreadModes.Unchanged); }
        catch (OpenCVException) { throw new InvalidDataException("画像を復号できません。"); }
        bool expectedSize = (image.Width == width && image.Height == height) ||
            (orient && image.Width == height && image.Height == width);
        if (image.Empty() || !expectedSize ||
            (image.Type() != MatType.CV_8UC1 && image.Type() != MatType.CV_8UC3 && image.Type() != MatType.CV_8UC4))
        {
            image.Dispose();
            throw new InvalidDataException("8 ビット以下の PNG または 8 ビット JPEG を指定してください。");
        }
        return image;
    }

    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngSignature);
    public static string MediaType(ReadOnlySpan<byte> bytes) => IsPng(bytes) ? "image/png" : "image/jpeg";
    public static byte[] ProviderInput(byte[] original, Mat decoded, bool normalizeJpeg) =>
        normalizeJpeg && !IsPng(original) ? Encode(decoded) : original;

    private static (uint Width, uint Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes.StartsWith(PngSignature) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes[12..16].SequenceEqual("IHDR"u8) && bytes[24] <= 8)
            return (BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        if (bytes.StartsWith<byte>([255, 216]))
        {
            int offset = 2;
            while (offset < bytes.Length && bytes[offset++] == 255)
            {
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (bytes.Length - offset < 3) break;
                byte marker = bytes[offset++];
                if (marker is 0 or 0xd8 or 0xd9 or 0xda) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || length > bytes.Length - offset) break;
                if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
                {
                    if (length < 8 || bytes[offset + 2] != 8) break;
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
                }
                offset += length;
            }
        }
        throw new InvalidDataException("有効な PNG または JPEG が必要です。");
    }

    public static Mat ToBgra(Mat source)
    {
        if (source.Channels() == 4) return source.Clone();
        var result = new Mat();
        Cv2.CvtColor(source, result, source.Channels() == 1 ? ColorConversionCodes.GRAY2BGRA : ColorConversionCodes.BGR2BGRA);
        return result;
    }

    public static Mat Crop(Mat source, int width, int height)
    {
        var cropWidth = Math.Min(source.Width, Math.Max(1, (int)Math.Round((double)source.Height * width / height)));
        var cropHeight = Math.Min(source.Height, Math.Max(1, (int)Math.Round((double)source.Width * height / width)));
        using var region = new Mat(source, new Rect((source.Width - cropWidth) / 2, (source.Height - cropHeight) / 2, cropWidth, cropHeight));
        var result = new Mat();
        Cv2.Resize(region, result, new Size(width, height), interpolation:
            cropWidth >= width && cropHeight >= height ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return result;
    }

    public static Mat Blend(Mat source, Mat styled, double strength)
    {
        using var original = ToBgra(source);
        using var edit = ToBgra(styled);
        var result = new Mat();
        Cv2.AddWeighted(original, 1 - strength, edit, strength, 0, result);
        using var alpha = new Mat();
        Cv2.ExtractChannel(original, alpha, 3);
        Cv2.InsertChannel(alpha, result, 3);
        return result;
    }

    public static Mat Composite(Mat foreground, Mat background)
    {
        if (foreground.Size() != background.Size() || foreground.Type() != MatType.CV_8UC4 ||
            background.Type() != MatType.CV_8UC4)
            throw new InvalidDataException("合成する前景と背景の寸法とチャンネル数が一致しません。");
        using var front = foreground.Clone();
        using var back = background.Clone();
        if (!front.GetArray(out Vec4b[] above) || !back.GetArray(out Vec4b[] below))
            throw new InvalidDataException("背景合成用の画素を読み込めません。");
        for (int i = 0; i < above.Length; i++)
        {
            var f = above[i];
            var b = below[i];
            double frontOpacity = f.Item3 / 255.0;
            double backOpacity = b.Item3 / 255.0 * (1 - frontOpacity);
            double opacity = frontOpacity + backOpacity;
            if (opacity == 0) { above[i] = default; continue; }
            byte Channel(byte frontValue, byte backValue) =>
                (byte)Math.Clamp(Math.Round((frontValue * frontOpacity + backValue * backOpacity) / opacity), 0, 255);
            above[i] = new(Channel(f.Item0, b.Item0), Channel(f.Item1, b.Item1), Channel(f.Item2, b.Item2),
                (byte)Math.Round(opacity * 255));
        }
        front.SetArray(above);
        return front.Clone();
    }

    public static byte[] Encode(Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out var bytes))
            throw new InvalidDataException("PNG に書き出せません。");
        return bytes;
    }

    public static byte[] EmbedRecipe(byte[] png, VisualRecipe recipe)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(recipe, JsonSerializerOptions.Web);
        ReadOnlySpan<byte> header = "TheoriaCast.Recipe\0\0\0\0\0"u8;
        const int ihdrEnd = 33;
        using var result = new MemoryStream();
        result.Write(png.AsSpan(0, ihdrEnd));
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, header.Length + json.Length);
        result.Write(number);
        result.Write("iTXt"u8);
        result.Write(header);
        result.Write(json);
        var crc = uint.MaxValue;
        foreach (var value in result.GetBuffer().AsSpan(ihdrEnd + 4, 4 + header.Length + json.Length))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        result.Write(number);
        result.Write(png.AsSpan(ihdrEnd));
        if (result.Length > VisualProtocol.MaxImageBytes)
            throw new InvalidDataException("書き出した画像が 16 MiB を超えました。");
        return result.ToArray();
    }
}
