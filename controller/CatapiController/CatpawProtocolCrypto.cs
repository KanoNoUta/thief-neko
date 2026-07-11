using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatapiController;

internal static partial class CatpawProtocolCrypto
{
    private const string XorKey = "ThisIsMyXorKey";
    private static readonly Lazy<string> PrivateKey = new(LoadPrivateKey);

    public static string DecryptResponse(string encryptedBody, string encryptedKey)
    {
        var cipherText = JsonSerializer.Deserialize<string>(encryptedBody)
            ?? throw new CryptographicException("Catpaw returned an invalid encrypted response.");
        byte[]? encryptedKeyBytes = null;
        byte[]? aesKeyBase64 = null;
        byte[]? aesKey = null;
        byte[]? cipherBytes = null;
        byte[]? clearBytes = null;
        try
        {
            encryptedKeyBytes = Convert.FromBase64String(encryptedKey);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PrivateKey.Value);
            aesKeyBase64 = rsa.Decrypt(encryptedKeyBytes, RSAEncryptionPadding.OaepSHA1);
            aesKey = Convert.FromBase64String(Encoding.UTF8.GetString(aesKeyBase64));
            cipherBytes = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            clearBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            Clear(encryptedKeyBytes);
            Clear(aesKeyBase64);
            Clear(aesKey);
            Clear(cipherBytes);
            Clear(clearBytes);
        }
    }

    private static string LoadPrivateKey()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(
            name => name.EndsWith("catpawCrypto.js", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Catpaw protocol resource is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = reader.ReadToEnd();
        var match = PrivateKeyPattern().Match(source);
        if (!match.Success)
        {
            throw new InvalidOperationException("Catpaw protocol key is unavailable.");
        }

        var encoded = Convert.FromBase64String(match.Groups[1].Value);
        try
        {
            for (var index = 0; index < encoded.Length; index++)
            {
                encoded[index] ^= (byte)XorKey[index % XorKey.Length];
            }
            return Encoding.UTF8.GetString(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    [GeneratedRegex("PRIVATE_KEY_ENCODED\\s*=\\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();
}
