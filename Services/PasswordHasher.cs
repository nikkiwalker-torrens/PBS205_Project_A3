using System;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Web.Services;

/// <summary>
/// Hashes and verifies passwords using PBKDF2-SHA256 with a random 16-byte salt.
/// Format stored in DB:  "v1:{base64salt}:{base64hash}"
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes   = 16;
    private const int HashBytes    = 32;
    private const int Iterations  = 200_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(password, salt);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split(':');
            if (parts.Length != 3 || parts[0] != "v1") return false;

            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Pbkdf2(password, salt);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Pbkdf2(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashBytes);
}
