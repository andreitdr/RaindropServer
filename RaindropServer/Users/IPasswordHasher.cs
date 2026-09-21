namespace RaindropServer.Users;

/// <summary>Hashes and verifies user passwords. Passwords are never stored or logged in the clear.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password into a self-describing string safe to persist.</summary>
    string Hash(string password);

    /// <summary>Verifies a password against a stored hash in constant time.</summary>
    bool Verify(string password, string encodedHash);
}
