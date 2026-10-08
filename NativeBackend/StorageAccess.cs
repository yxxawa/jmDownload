using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DesktopShell.NativeBackend;

public sealed class StorageAccessException(string message, Exception inner) : IOException(message, inner);

public static class StorageAccess
{
    public static void RequireWritableDirectory(string directory, string purpose)
    {
        var probe = Path.Combine(directory, ".jm-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.WriteByte(0);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new StorageAccessException($"{purpose}无法写入：{directory}。请改用可写目录；安卓公共存储需要授予文件访问权限。", e);
        }
        finally { try { File.Delete(probe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    public static string ImageCacheDirectory(string appDataRoot)
    {
        var preferred = Path.Combine(appDataRoot, "Cache", "reader");
        try { RequireWritableDirectory(preferred, "图片缓存目录"); return preferred; }
        catch (StorageAccessException)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(appDataRoot))))[..16];
            var fallback = Path.Combine(Path.GetTempPath(), "JMDownload", "image-cache", key);
            RequireWritableDirectory(fallback, "备用图片缓存目录");
            return fallback;
        }
    }

    public static string Describe(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is StorageAccessException) return current.Message;
            if (current is UnauthorizedAccessException)
                return "本地文件访问被拒绝，请检查缓存或下载目录的写入权限；安卓公共存储需要授予文件访问权限。 " + current.Message;
        }
        return error is OperationCanceledException ? "请求超时，请检查网络连接后重试。" : error.Message;
    }
}
