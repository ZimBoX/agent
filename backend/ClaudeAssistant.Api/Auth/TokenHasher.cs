using System.Security.Cryptography;
using System.Text;

namespace ClaudeAssistant.Api.Auth;

public static class TokenHasher
{
    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public static string GenerateEnrollmentCode()
    {
        // Без символов, которые легко перепутать при вводе (0/O, 1/I)
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return new string(chars);
    }
}
