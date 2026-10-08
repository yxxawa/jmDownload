using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace DesktopShell.NativeBackend;

public static class JmImageDecoder
{
    private const int Scramble268850 = 268850;
    private const int Scramble421926 = 421926;

    public static int GetSegmentCount(string? scrambleId, string aid, string fileNameWithoutSuffix)
    {
        if (!int.TryParse(scrambleId, out var scramble) || !int.TryParse(aid, out var photoId))
        {
            return 0;
        }

        if (photoId < scramble)
        {
            return 0;
        }

        if (photoId < Scramble268850)
        {
            return 10;
        }

        var x = photoId < Scramble421926 ? 10 : 8;
        var md5 = JmCrypto.Md5Hex(photoId + fileNameWithoutSuffix);
        var value = md5[^1] % x;
        return value * 2 + 2;
    }

    public static void DecodeAndSave(byte[] bytes, int segments, string savePath)
    {
        var source = LoadBitmap(bytes);
        if (segments > 0)
        {
            source = ReorderSegments(source, segments);
        }

        SaveBitmap(source, savePath);
    }

    public static bool HasImageSignature(byte[] data) => data.Length >= 12 &&
        ((data[0] == 0xff && data[1] == 0xd8) || (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4e && data[3] == 0x47) ||
         System.Text.Encoding.ASCII.GetString(data, 0, 3) == "GIF" || System.Text.Encoding.ASCII.GetString(data, 0, 2) == "BM" ||
         (System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP"));
    public static bool IsUsableBytes(byte[] data, string suffix)
    {
        if (!HasImageSignature(data)) return false;
        try { var image = LoadBitmap(data); return image.PixelWidth > 0 && image.PixelHeight > 0 && (long)image.PixelWidth * image.PixelHeight <= 64_000_000; }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException) { return false; }
    }

    public static bool IsUsableImage(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return false;
            return LoadBitmap(File.ReadAllBytes(path)).PixelWidth > 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or System.IO.FileFormatException) { return false; }
    }

    public static PdfImageData LoadPdfImage(string imagePath)
    {
        var source = LoadBitmap(File.ReadAllBytes(imagePath));
        var jpeg = EncodeJpeg(source, quality: 94);
        return new PdfImageData(jpeg, source.PixelWidth, source.PixelHeight);
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        if (bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return LoadWebP(bytes);
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource LoadWebP(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("WebP 数据无效或不完整");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64_000_000)
            throw new InvalidDataException("WebP 图片尺寸无效或超过 6400 万像素");
        var pixels = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(pixels);
        var result = codec.GetPixels(pixels, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException("WebP 解码失败：" + result);
        var source = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static BitmapSource ReorderSegments(BitmapSource source, int segments)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var decoded = new byte[pixels.Length];
        source.CopyPixels(pixels, stride, 0);

        var over = height % segments;
        for (var i = 0; i < segments; i++)
        {
            var move = height / segments;
            var ySource = height - (move * (i + 1)) - over;
            var yDestination = move * i;

            if (i == 0)
            {
                move += over;
            }
            else
            {
                yDestination += over;
            }

            Buffer.BlockCopy(
                pixels,
                ySource * stride,
                decoded,
                yDestination * stride,
                move * stride);
        }

        var result = BitmapSource.Create(
            width,
            height,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            decoded,
            stride);
        result.Freeze();
        return result;
    }

    private static void SaveBitmap(BitmapSource source, string savePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        var suffix = Path.GetExtension(savePath).ToLowerInvariant();
        if (suffix == ".webp") { SaveWebP(source, savePath); return; }
        BitmapEncoder encoder = suffix switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        if (encoder is JpegBitmapEncoder)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            converted.Freeze();
            source = converted;
        }

        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(savePath);
        encoder.Save(file);
    }

    private static void SaveWebP(BitmapSource source, string savePath)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze(); source = converted;
        }
        using var bitmap = new SKBitmap(new SKImageInfo(source.PixelWidth, source.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var pixels = new byte[bitmap.ByteCount];
        source.CopyPixels(pixels, bitmap.RowBytes, 0);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 95) ?? throw new InvalidDataException("WebP 编码失败");
        using var file = File.Create(savePath);
        encoded.SaveTo(file);
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        if (source.Format != PixelFormats.Bgr24)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            converted.Freeze();
            source = converted;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

public sealed record PdfImageData(byte[] JpegBytes, int Width, int Height);
