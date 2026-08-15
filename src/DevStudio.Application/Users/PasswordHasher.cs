using System.Security.Cryptography;

namespace DevStudio.Application.Users;

/// <summary>
/// PBKDF2-SHA256 password hashing. Hand-rolled for the same reason the cron parser is: it keeps the
/// dependency list short and the behaviour easy to test. The hash is self-describing —
/// <c>v1.iterations.salt.key</c>, base64 — so the iteration count can be raised later without
/// invalidating passwords already stored.
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int DefaultIterations = 210_000;

    /// <summary>
    /// Shortest password accepted. Low on purpose: the account seeded on a fresh install is
    /// <c>admin</c>/<c>admin</c>, and a minimum that rejected it would mean the first thing the
    /// operator does is locked out of being undone.
    /// </summary>
    public const int MinimumLength = 4;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, KeyBytes);

        return $"v1.{DefaultIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// True when <paramref name="password"/> produced <paramref name="hash"/>. A malformed or empty
    /// hash is a false, never an exception: a corrupt record must refuse the sign-in, not break it.
    /// </summary>
    public static bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(hash))
            return false;

        var parts = hash.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
