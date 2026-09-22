namespace RaindropServer.E2ee;

// Byte arrays travel as base64 in JSON and as binary in MessagePack.

// ── Devices and identity keys ────────────────────────────────────────────────

/// <param name="Certificate">The device certificate, signed by the user's identity key.</param>
public record RegisterDeviceRequest(byte[] Certificate);

/// <param name="UserIds">Whose identity keys and devices to fetch. At most 500.</param>
public record ListDevicesRequest(IReadOnlyList<Guid> UserIds);

/// <param name="DeviceId">The device to revoke; one of the caller's own.</param>
public record RevokeDeviceRequest(Guid DeviceId);

/// <param name="Password">The account password, confirming an irreversible reset.</param>
public record ResetIdentityRequest(string Password);

/// <param name="DeviceId">The device, one of the caller's own.</param>
/// <param name="Certificate">Its certificate, exactly as uploaded; clients verify it themselves.</param>
/// <param name="CreatedAtUtc">When it was registered.</param>
public record DeviceSummary(Guid DeviceId, byte[] Certificate, DateTime CreatedAtUtc);

/// <param name="UserId">The user.</param>
/// <param name="IdentityKey">Their public identity key, or null before their first device is registered.</param>
/// <param name="Devices">Their active devices, oldest first.</param>
public record UserDevices(Guid UserId, byte[]? IdentityKey, IReadOnlyList<DeviceSummary> Devices);

public record ListDevicesResponse(IReadOnlyList<UserDevices> Users);

// ── Binding a session to a device ────────────────────────────────────────────

/// <param name="Nonce">32 random bytes to sign, valid for <see cref="ExpiresAtUtc"/>, usable once.</param>
public record SessionChallengeResponse(byte[] Nonce, DateTime ExpiresAtUtc);

/// <param name="DeviceId">The device this session runs on.</param>
/// <param name="Signature">
/// The device signature key's Ed25519 signature over <c>"raindrop/session-binding/v1\n" ‖ nonce</c>.
/// </param>
public record BindSessionRequest(Guid DeviceId, byte[] Signature);

// ── Key backup ───────────────────────────────────────────────────────────────

/// <param name="Blob">The sealed backup, as the client produced it. At most 64 KiB.</param>
public record PutKeyBackupRequest(byte[] Blob);

/// <param name="Blob">The sealed backup, or null when the user has none.</param>
/// <param name="UpdatedAtUtc">When it was stored.</param>
public record KeyBackupResponse(byte[]? Blob, DateTime? UpdatedAtUtc);

// ── Linking a new device ─────────────────────────────────────────────────────

/// <param name="Offer">The new device's link offer: opaque to the server, at most 4 KiB.</param>
public record StartDeviceLinkRequest(byte[] Offer);

/// <param name="LinkId">What the new device shows (in its link code) for an existing device to fetch.</param>
/// <param name="ExpiresAtUtc">When the link lapses.</param>
public record StartDeviceLinkResponse(string LinkId, DateTime ExpiresAtUtc);

public record DeviceLinkRequest(string LinkId);

/// <param name="Offer">The new device's offer, for an existing device to check against the link code.</param>
public record DeviceLinkOfferResponse(byte[] Offer);

/// <param name="LinkId">The link being approved.</param>
/// <param name="Approval">The existing device's answer, encrypted to the new device. At most 8 KiB.</param>
public record ApproveDeviceLinkRequest(string LinkId, byte[] Approval);

/// <param name="Approval">The approval once an existing device has given it, else null.</param>
public record DeviceLinkApprovalResponse(byte[]? Approval);

// ── Events ───────────────────────────────────────────────────────────────────

/// <summary>Event types, open strings like every other event name.</summary>
public static class E2eeEventTypes
{
    /// <summary>A user registered a device. Data: <see cref="DeviceEvent"/>.</summary>
    public const string DeviceAdded = "e2ee.device.added";

    /// <summary>A device was revoked. Data: <see cref="DeviceEvent"/>.</summary>
    public const string DeviceRevoked = "e2ee.device.revoked";

    /// <summary>
    /// A user replaced their identity key, revoking every device. Clients show that user's safety
    /// number as changed. Data: <see cref="IdentityResetEvent"/>.
    /// </summary>
    public const string IdentityReset = "e2ee.identity.reset";
}

/// <param name="UserId">Whose device.</param>
/// <param name="DeviceId">Which device.</param>
/// <param name="Certificate">Its certificate.</param>
public record DeviceEvent(Guid UserId, Guid DeviceId, byte[] Certificate);

/// <param name="UserId">The user whose identity key was reset.</param>
public record IdentityResetEvent(Guid UserId);
