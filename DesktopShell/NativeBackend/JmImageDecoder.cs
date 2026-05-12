using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    public static PdfImageData LoadPdfImage(string imagePath)
    {
        var source = LoadBitmap(File.ReadAllBytes(imagePath));
        var jpeg = EncodeJpeg(source, quality: 94);
        return new PdfImageData(jpeg, source.PixelWidth, source.PixelHeight);
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
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
