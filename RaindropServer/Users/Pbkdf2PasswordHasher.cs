using System.Security.Cryptography;

namespace RaindropServer.Users;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing with a per-password random salt.
/// The encoded form is <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c> (both parts Base64),
/// which is self-describing: the iteration count travels with the hash, so it can be raised later
/// without invalidating existing passwords.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>OWASP's recommended PBKDF2-SHA256 work factor.</summary>
    private const int DefaultIterations = 210_000;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        if (iterations < 1_000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "Refusing to hash passwords with fewer than 1000 iterations.");

        _iterations = iterations;
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, _iterations);

        return $"{Prefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');

        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations))
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

        var actual = Derive(password, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
}
