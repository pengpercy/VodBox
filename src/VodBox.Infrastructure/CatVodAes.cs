using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>AES-CBC wire format shared by the verified CatVod HTTP protocols.</summary>
internal static class CatVodAes
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Encrypt(string plaintext, string key, string iv, bool wrapped = false)
    {
        using var aes = Aes.Create(); aes.Key = Utf8.GetBytes(key);
        string result = Convert.ToBase64String(aes.EncryptCbc(Utf8.GetBytes(plaintext), Utf8.GetBytes(iv), PaddingMode.PKCS7));
        // Android Base64.DEFAULT uses LF, including a final LF; NO_WRAP does neither.
        return wrapped ? string.Concat(Enumerable.Range(0, (result.Length + 75) / 76).Select(index => result.Substring(index * 76, Math.Min(76, result.Length - index * 76)) + "\n")) : result;
    }
    internal static string Decrypt(string value, string key, string iv)
    {
        using var aes = Aes.Create(); aes.Key = Utf8.GetBytes(key);
        return Utf8.GetString(aes.DecryptCbc(Convert.FromBase64String(value), Utf8.GetBytes(iv), PaddingMode.PKCS7));
    }
}
