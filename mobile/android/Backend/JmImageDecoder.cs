using Android.Graphics;
using Android.OS;
using Path = System.IO.Path;

namespace DesktopShell.NativeBackend;

/// <summary>
/// 电脑端用 WPF 的 BitmapSource 做图片解码与分段还原；Android 没有 WPF，
/// 这里用 Android.Graphics.Bitmap 实现同样的算法，公开 API 与电脑端逐一对应。
/// </summary>
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
        var source = Decode(bytes) ?? throw new IOException("图片解码失败");
        Bitmap? reordered = null;
        try
        {
            if (segments > 1) reordered = ReorderSegments(source, segments);
            var target = reordered ?? source;
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            using var file = File.Create(savePath);
            if (!target.Compress(SaveFormatFor(savePath), 95, file))
            {
                throw new IOException("图片编码失败: " + Path.GetExtension(savePath));
            }
        }
        finally
        {
            if (reordered is not null && !reordered.IsRecycled) reordered.Recycle();
            if (!source.IsRecycled) source.Recycle();
        }
    }

    public static bool HasImageSignature(byte[] data) => data.Length >= 12 &&
        ((data[0] == 0xff && data[1] == 0xd8) || (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4e && data[3] == 0x47) ||
         System.Text.Encoding.ASCII.GetString(data, 0, 3) == "GIF" || System.Text.Encoding.ASCII.GetString(data, 0, 2) == "BM" ||
         (System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP"));

    public static bool IsUsableBytes(byte[] data, string suffix)
    {
        if (!HasImageSignature(data)) return false;
        var size = Probe(data);
        return size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height <= 64_000_000;
    }

    public static bool IsUsableImage(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return false;
            var size = Probe(File.ReadAllBytes(path));
            return size.Width > 0 && size.Height > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException)
        {
            return false;
        }
    }

    public static PdfImageData LoadPdfImage(string imagePath)
    {
        var source = Decode(File.ReadAllBytes(imagePath)) ?? throw new IOException("图片解码失败: " + imagePath);
        try
        {
            using var stream = new MemoryStream();
            source.Compress(Bitmap.CompressFormat.Jpeg!, 94, stream);
            return new PdfImageData(stream.ToArray(), source.Width, source.Height);
        }
        finally
        {
            if (!source.IsRecycled) source.Recycle();
        }
    }

    /// <summary>只读图片头，不分配整张位图。</summary>
    private static (int Width, int Height) Probe(byte[] data)
    {
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(data, 0, data.Length, options);
        return (options.OutWidth, options.OutHeight);
    }

    private static Bitmap? Decode(byte[] data)
    {
        var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
        var bitmap = BitmapFactory.DecodeByteArray(data, 0, data.Length, options);
        if (bitmap is null) return null;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && bitmap.GetConfig() == Bitmap.Config.Hardware)
        {
            // 硬件位图不支持 GetPixels，分段还原必须在软件位图上做。
            var software = bitmap.Copy(Bitmap.Config.Argb8888!, false);
            bitmap.Recycle();
            return software;
        }
        return bitmap;
    }

    /// <summary>按行分块重排：与电脑端同一套算法，但只用一个分块大小的缓冲区，内存占用低得多。</summary>
    private static Bitmap? ReorderSegments(Bitmap source, int segments)
    {
        var width = source.Width;
        var height = source.Height;
        var move = height / segments;
        if (segments <= 1 || move <= 0) return null;

        var over = height % segments;
        var result = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        if (result is null) return null;

        var buffer = new int[width * (move + over)];
        for (var i = 0; i < segments; i++)
        {
            var block = move;
            var ySource = height - (move * (i + 1)) - over;
            var yDestination = move * i;
            if (i == 0)
            {
                block += over;
            }
            else
            {
                yDestination += over;
            }

            source.GetPixels(buffer, 0, width, 0, ySource, width, block);
            result.SetPixels(buffer, 0, width, 0, yDestination, width, block);
        }

        return result;
    }

    private static Bitmap.CompressFormat SaveFormatFor(string savePath) => Path.GetExtension(savePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => Bitmap.CompressFormat.Jpeg!,
        ".webp" => Bitmap.CompressFormat.Webp!,
        // Android 的 Bitmap 没有 BMP / GIF 编码器，统一退回 PNG；GIF 源图在下载流程里会直接原样保存。
        _ => Bitmap.CompressFormat.Png!,
    };
}

public sealed record PdfImageData(byte[] JpegBytes, int Width, int Height);
