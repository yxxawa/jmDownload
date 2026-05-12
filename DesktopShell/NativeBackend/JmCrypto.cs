using System.Security.Cryptography;
using System.Text;

namespace DesktopShell.NativeBackend;

public static class JmCrypto
{
    public const string AppVersion = "2.0.21";
    public const string AppTokenSecret = "18comicAPP";
    public const string AppTokenSecretContent = "18comicAPPContent";
    public const string AppDataSecret = "185Hcomic3PAPP7R";
    public const string ApiDomainServerSecret = "diosfjckwpqpdfjkvnqQjsik";

    public static (string Token, string TokenParam) TokenAndTokenParam(string ts, string? secret = null)
    {
        secret ??= AppTokenSecret;
        return (Md5Hex(ts + secret), ts + "," + AppVersion);
    }

    public static string DecodeResponseData(string data, string ts, string? secret = null)
    {
        secret ??= AppDataSecret;
        var encrypted = Convert.FromBase64String(data);
        var key = Encoding.UTF8.GetBytes(Md5Hex(ts + secret));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        if (decrypted.Length == 0)
        {
            return string.Empty;
        }

        var padding = decrypted[^1];
        var length = decrypted.Length - padding;
        if (padding <= 0 || length < 0)
        {
            length = decrypted.Length;
        }

        return Encoding.UTF8.GetString(decrypted, 0, length);
    }

    public static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
