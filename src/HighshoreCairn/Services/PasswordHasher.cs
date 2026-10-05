using System.Security.Cryptography;
using System.Text;
using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>
/// Password hashing with PBKDF2-HMAC-SHA256 (System.Security.Cryptography, no external packages).
/// Each account has its own random salt; verification is constant-time.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static void SetPassword(Account account, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        account.Iterations = DefaultIterations;
        account.PasswordSalt = Convert.ToBase64String(salt);
        account.PasswordHash = Convert.ToBase64String(Derive(password, salt, account.Iterations));
    }

    public static bool Verify(Account account, string password)
    {
        if (string.IsNullOrEmpty(account.PasswordHash) || string.IsNullOrEmpty(account.PasswordSalt)) return false;
        try
        {
            var salt = Convert.FromBase64String(account.PasswordSalt);
            var expected = Convert.FromBase64String(account.PasswordHash);
            var iterations = account.Iterations > 0 ? account.Iterations : DefaultIterations;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
