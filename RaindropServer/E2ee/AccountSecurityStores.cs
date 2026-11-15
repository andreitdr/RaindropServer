namespace RaindropServer.E2ee;

/// <summary>
/// What a person keeps on this server so they can get their identity back: their root secret sealed
/// under their recovery key, and their vault sealed under the root. Both are opaque here.
/// </summary>
/// <param name="UserId">The account they belong to.</param>
/// <param name="LookupId">
/// The name the sealed root is fetched by before signing in: derived by the client from the recovery
/// key and this server's identity key, so it means nothing anywhere else.
/// </param>
/// <param name="SealedRoot">The root secret, sealed under the recovery key.</param>
/// <param name="Vault">The vault, sealed under the root.</param>
/// <param name="UpdatedAtUtc">When it was last replaced.</param>
public sealed record IdentityVaultRecord(Guid UserId, string LookupId, byte[] SealedRoot, byte[] Vault, DateTime UpdatedAtUtc);

/// <summary>Persistence for identity vaults: at most one per user.</summary>
public interface IIdentityVaultStore
{
    Task<IdentityVaultRecord?> FindAsync(Guid userId, CancellationToken cancellationToken);

    Task<IdentityVaultRecord?> FindByLookupAsync(string lookupId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the user's vault, replacing any earlier one. Returns false, changing nothing, when the
    /// lookup id already belongs to another user.
    /// </summary>
    Task<bool> PutAsync(IdentityVaultRecord vault, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>An account's authenticator: the one secret here the server can read, because it has to check codes against it.</summary>
/// <param name="UserId">The account.</param>
/// <param name="Secret">The TOTP secret.</param>
/// <param name="BackupCodeHashes">Hashes of the one-time backup codes not used yet.</param>
/// <param name="LastUsedStep">The newest 30-second step a code was accepted for; nothing at or before it is accepted again.</param>
/// <param name="CreatedAtUtc">When it was enrolled.</param>
public sealed record AuthenticatorRecord(
    Guid UserId, byte[] Secret, IReadOnlyList<string> BackupCodeHashes, long LastUsedStep, DateTime CreatedAtUtc);

/// <summary>Persistence for authenticators: at most one per user.</summary>
public interface IAuthenticatorStore
{
    Task<AuthenticatorRecord?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores the user's authenticator, replacing any earlier one.</summary>
    Task PutAsync(AuthenticatorRecord authenticator, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the last used step forward to <paramref name="step"/>. Returns false when it is already
    /// there or beyond — which is how a code is kept from being accepted twice, by two requests at once
    /// as much as by one after the other.
    /// </summary>
    Task<bool> TryAdvanceStepAsync(Guid userId, long step, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the unused backup codes with <paramref name="remaining"/>, provided they are still
    /// exactly <paramref name="expected"/>. Returns false when another request changed them first.
    /// </summary>
    Task<bool> TryReplaceBackupCodesAsync(
        Guid userId, IReadOnlyList<string> expected, IReadOnlyList<string> remaining, CancellationToken cancellationToken);
}
