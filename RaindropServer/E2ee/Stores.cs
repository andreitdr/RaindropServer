namespace RaindropServer.E2ee;

/// <summary>
/// A user's public identity key. The server stores it so other users can fetch it, and refuses a
/// device whose certificate was signed by any other key. It holds no private key and cannot sign.
/// </summary>
/// <param name="UserId">The user it belongs to.</param>
/// <param name="IdentityKey">The Ed25519 public key (32 bytes).</param>
/// <param name="UpdatedAtUtc">When it was set, or last replaced by an identity reset.</param>
public sealed record UserIdentityRecord(Guid UserId, byte[] IdentityKey, DateTime UpdatedAtUtc);

/// <summary>One of a user's devices, as its certificate describes it.</summary>
/// <param name="UserId">The owner.</param>
/// <param name="DeviceId">The client-generated id of this device on this server.</param>
/// <param name="SignatureKey">The device's Ed25519 MLS signature key, from the certificate.</param>
/// <param name="Certificate">The certificate exactly as the client uploaded it; clients verify it themselves.</param>
/// <param name="CreatedAtUtc">When it was registered.</param>
/// <param name="RevokedAtUtc">When it was revoked, or null while it is in use.</param>
public sealed record DeviceRecord(
    Guid UserId,
    Guid DeviceId,
    byte[] SignatureKey,
    byte[] Certificate,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc)
{
    public bool IsActive => RevokedAtUtc is null;
}

/// <summary>
/// A user's sealed key backup. Only their recovery key or passphrase opens it; the server
/// stores it without being able to read it.
/// </summary>
public sealed record KeyBackupRecord(Guid UserId, byte[] Blob, DateTime UpdatedAtUtc);

/// <summary>Persistence for identity keys and devices.</summary>
public interface IDeviceStore
{
    Task<UserIdentityRecord?> FindIdentityAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserIdentityRecord>> FindIdentitiesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>
    /// Records a user's identity key if they have none yet. Returns false, changing nothing, when
    /// one is already stored — even the same one.
    /// </summary>
    Task<bool> TryCreateIdentityAsync(UserIdentityRecord identity, CancellationToken cancellationToken);

    /// <summary>Overwrites a user's identity key.</summary>
    Task ReplaceIdentityAsync(UserIdentityRecord identity, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets a user's identity key (an identity reset, or erasing a user who left), so the next
    /// device they register establishes a new one.
    /// </summary>
    Task DeleteIdentityAsync(Guid userId, CancellationToken cancellationToken);

    Task<DeviceRecord?> FindDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    /// <summary>The devices of the given users, oldest first, optionally including revoked ones.</summary>
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        IReadOnlyCollection<Guid> userIds, bool includeRevoked, CancellationToken cancellationToken);

    /// <summary>Adds a device. Returns false when that user already has a device with this id.</summary>
    Task<bool> AddDeviceAsync(DeviceRecord device, CancellationToken cancellationToken);

    /// <summary>Marks one device revoked. Returns false when it did not exist or was already revoked.</summary>
    Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>Revokes every active device of a user, returning how many there were.</summary>
    Task<int> RevokeAllDevicesAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken);
}

/// <summary>Persistence for sealed key backups: at most one per user.</summary>
public interface IKeyBackupStore
{
    Task<KeyBackupRecord?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores the user's backup, replacing any earlier one.</summary>
    Task PutAsync(KeyBackupRecord backup, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);
}
