using System.Security.Cryptography;
using System.Text;

namespace ClaudeAssistant.Client.Services;

public static class TokenStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeAssistant",
        "device_token.bin"
    );

    public static bool TryLoad(out string token)
    {
        token = string.Empty;
        if (!File.Exists(FilePath))
        {
            return false;
        }

        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            token = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(token);
        }
        catch (CryptographicException)
        {
            // Файл повреждён или зашифрован под другим Windows-пользователем — считаем, что токена нет
            return false;
        }
    }

    public static void Save(string token)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var plain = Encoding.UTF8.GetBytes(token);
        var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    public static string GenerateNewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes);
    }
}
