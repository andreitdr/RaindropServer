using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RaindropServer.Events;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.E2ee;

/// <summary>
/// Every rule about end-to-end encryption identities on this server: which identity key a user has,
/// which devices belong to them, which session runs on which device, the sealed key backup, and the
/// relay for linking a new device.
/// </summary>
/// <remarks>
/// The server holds public keys and opaque blobs only; it can never sign as a user or read what they
/// encrypt. What it enforces is access, the part it has to be trusted with anyway:
/// <list type="bullet">
/// <item>A device is accepted only with a certificate its user's identity key signed. The first
/// device a user registers establishes that key.</item>
/// <item>A password opens a session; only a device's key <em>binds</em> one (see
/// <see cref="BindSessionAsync"/>). Revoking devices and replacing the backup need a bound session,
/// so somebody holding only the password cannot remove the real owner's devices or destroy their
/// backup.</item>
/// <item>Clients check every certificate and identity key themselves against what they pinned; the
/// server's checks are defence in depth, not the guarantee.</item>
/// </list>
/// </remarks>
public sealed class DeviceService
{
    private static readonly byte[] BindingContext = "raindrop/session-binding/v1\n"u8.ToArray();
    private const int NonceBytes = 32;
    private const int MaximumOfferBytes = 4 * 1024;
    private const int MaximumApprovalBytes = 8 * 1024;

    private readonly IDeviceStore _devices;
    private readonly IKeyBackupStore _backups;
    private readonly IUserStore _users;
    private readonly IPasswordHasher _hasher;
    private readonly ISessionStore _sessions;
    private readonly IDeviceLinkRelay _links;
    private readonly IEventPublisher _events;
    private readonly IReadOnlyList<IDeviceListener> _deviceListeners;
    private readonly IReadOnlyList<ISessionListener> _sessionListeners;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    // Outstanding challenges, keyed by the session token they were issued to.
    private readonly ConcurrentDictionary<string, (byte[] Nonce, DateTime ExpiresAtUtc)> _challenges =
        new(StringComparer.Ordinal);

    public DeviceService(
        IDeviceStore devices,
        IKeyBackupStore backups,
        IUserStore users,
        IPasswordHasher hasher,
        ISessionStore sessions,
        IDeviceLinkRelay links,
        IEventPublisher events,
        E2eeOptions options,
        IEnumerable<IDeviceListener>? deviceListeners = null,
        IEnumerable<ISessionListener>? sessionListeners = null,
        TimeProvider? time = null)
    {
        _devices = devices;
        _backups = backups;
        _users = users;
        _hasher = hasher;
        _sessions = sessions;
        _links = links;
        _events = events;
        _options = options;
        _deviceListeners = deviceListeners?.ToList() ?? [];
        _sessionListeners = sessionListeners?.ToList() ?? [];
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Devices ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers one of the caller's devices. The certificate must be about the caller and signed by
    /// their identity key. When they have none yet, this, their first device, establishes it.
    /// Registering the same certificate again is a no-op.
    /// </summary>
    public async Task<DeviceSummary> RegisterDeviceAsync(
        UserSession session, RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);

        var certificate = DeviceCertificate.Parse(request.Certificate);
        if (certificate.UserId != session.UserId)
            throw ApiException.Forbidden("certificate_mismatch", "That certificate is for a different user.");
        if (!certificate.IsSignatureValid())
            throw ApiException.BadRequest("invalid_certificate", "The certificate's signature does not verify.");

        var now = UtcNow;
        var identity = await _devices.FindIdentityAsync(session.UserId, cancellationToken);
        if (identity is null)
        {
            await _devices.TryCreateIdentityAsync(
                new UserIdentityRecord(session.UserId, certificate.UserIdentityKey, now), cancellationToken);
            identity = await _devices.FindIdentityAsync(session.UserId, cancellationToken);
        }

        if (identity is null || !CryptographicOperations.FixedTimeEquals(identity.IdentityKey, certificate.UserIdentityKey))
            throw ApiException.Conflict("identity_mismatch",
                "That certificate was signed by a different identity key than this account's. Link this device " +
                "from one you already have, restore your key backup, or reset your identity.");

        if (await _devices.FindDeviceAsync(session.UserId, certificate.DeviceId, cancellationToken) is { } existing)
        {
            if (existing.IsActive && existing.Certificate.AsSpan().SequenceEqual(request.Certificate))
                return ToSummary(existing);

            throw ApiException.Conflict("device_exists", "This account already has a device with that id.");
        }

        var active = await _devices.ListDevicesAsync([session.UserId], includeRevoked: false, cancellationToken);
        if (active.Count >= _options.MaximumDevicesPerUser)
            throw ApiException.Conflict("too_many_devices",
                $"An account may have at most {_options.MaximumDevicesPerUser} devices. Revoke one first.");

        var device = new DeviceRecord(
            session.UserId, certificate.DeviceId, certificate.DeviceSignatureKey, request.Certificate, now, null);

        if (!await _devices.AddDeviceAsync(device, cancellationToken))
            throw ApiException.Conflict("device_exists", "This account already has a device with that id.");

        await NotifyAsync(listener => listener.OnDeviceAddedAsync(device, cancellationToken));
        await _events.PublishAsync(E2eeEventTypes.DeviceAdded,
            new DeviceEvent(device.UserId, device.DeviceId, device.Certificate));

        return ToSummary(device);
    }

    /// <summary>The identity keys and active devices of the given users: what a client needs to check them.</summary>
    public async Task<ListDevicesResponse> ListDevicesAsync(
        UserSession session, ListDevicesRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);

        var userIds = (request.UserIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (userIds.Count > _options.MaximumUsersPerListing)
            throw ApiException.BadRequest("too_many_users",
                $"Ask about at most {_options.MaximumUsersPerListing} users at once.");

        var identities = (await _devices.FindIdentitiesAsync(userIds, cancellationToken))
            .ToDictionary(identity => identity.UserId);
        var devices = (await _devices.ListDevicesAsync(userIds, includeRevoked: false, cancellationToken))
            .ToLookup(device => device.UserId);

        return new ListDevicesResponse(userIds
            .Select(id => new UserDevices(
                id,
                identities.GetValueOrDefault(id)?.IdentityKey,
                devices[id].Select(ToSummary).ToList()))
            .ToList());
    }

    /// <summary>
    /// Revokes one of the caller's devices and ends its sessions. Needs a session bound to a device,
    /// unless the device being revoked is the caller's own.
    /// </summary>
    public async Task<MessageResponse> RevokeDeviceAsync(
        UserSession session, RevokeDeviceRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        if (session.DeviceId != request.DeviceId)
            RequireBound(session);

        var device = await _devices.FindDeviceAsync(session.UserId, request.DeviceId, cancellationToken);
        if (device is null || !device.IsActive)
            throw ApiException.NotFound("device_not_found", "No active device of yours has that id.");

        var now = UtcNow;
        if (!await _devices.RevokeDeviceAsync(session.UserId, request.DeviceId, now, cancellationToken))
            throw ApiException.NotFound("device_not_found", "No active device of yours has that id.");

        await AfterRevokedAsync(device with { RevokedAtUtc = now }, cancellationToken);
        return new MessageResponse("The device was revoked.");
    }

    /// <summary>
    /// Replaces the caller's identity: forgets their identity key and backup and revokes every device,
    /// so the next device they register establishes a new key. This is the way back for somebody who
    /// lost every device <em>and</em> their recovery key. It needs the password, and everyone sharing a
    /// channel with them is told, because their safety number changes.
    /// </summary>
    public async Task<MessageResponse> ResetIdentityAsync(
        UserSession session, ResetIdentityRequest request, CancellationToken cancellationToken)
    {
        var user = await RequireActiveAsync(session, cancellationToken);
        if (user.PasswordHash is null || !_hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw ApiException.Unauthorized("invalid_credentials", "The password is incorrect.");

        await EraseAsync(session.UserId, cancellationToken);
        await _events.PublishAsync(E2eeEventTypes.IdentityReset, new IdentityResetEvent(session.UserId));

        return new MessageResponse("Your encryption identity was reset. Register this device to set up a new one.");
    }

    /// <summary>
    /// Forgets everything end-to-end encryption held for a user: identity key, backup, devices and the
    /// sessions bound to them. Used by an identity reset and when an account is erased.
    /// </summary>
    public async Task EraseAsync(Guid userId, CancellationToken cancellationToken)
    {
        var devices = await _devices.ListDevicesAsync([userId], includeRevoked: false, cancellationToken);
        var now = UtcNow;

        await _devices.RevokeAllDevicesAsync(userId, now, cancellationToken);
        await _devices.DeleteIdentityAsync(userId, cancellationToken);
        await _backups.DeleteAsync(userId, cancellationToken);

        foreach (var device in devices)
            await AfterRevokedAsync(device with { RevokedAtUtc = now }, cancellationToken);

        await NotifyAsync(listener => listener.OnIdentityResetAsync(userId, cancellationToken));
    }

    private async Task AfterRevokedAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        _sessions.RemoveAllForDevice(device.UserId, device.DeviceId);
        foreach (var listener in _sessionListeners)
        {
            try
            {
                await listener.OnSessionsChangedAsync(device.UserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[raindrop] {listener.GetType().Name} failed for member {device.UserId}: {ex.Message}");
            }
        }

        await NotifyAsync(listener => listener.OnDeviceRevokedAsync(device, cancellationToken));
        await _events.PublishAsync(E2eeEventTypes.DeviceRevoked,
            new DeviceEvent(device.UserId, device.DeviceId, device.Certificate));
    }

    // ── Binding a session to a device ────────────────────────────────────────

    /// <summary>Issues a one-time nonce for this session to sign with a device key.</summary>
    public async Task<SessionChallengeResponse> CreateChallengeAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);

        var now = UtcNow;
        foreach (var (token, challenge) in _challenges)
        {
            if (challenge.ExpiresAtUtc <= now)
                _challenges.TryRemove(token, out _);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var expires = now + _options.ChallengeLifetime;
        _challenges[session.Token] = (nonce, expires);
        return new SessionChallengeResponse(nonce, expires);
    }

    /// <summary>
    /// Binds the session to one of its user's active devices, given that device's signature over the
    /// session's outstanding challenge. The challenge is used up whether or not the signature is good.
    /// </summary>
    public async Task<MessageResponse> BindSessionAsync(
        UserSession session, BindSessionRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);

        if (!_challenges.TryRemove(session.Token, out var challenge) || challenge.ExpiresAtUtc <= UtcNow)
            throw ApiException.BadRequest("no_challenge", "Ask for a challenge first; it lapses after a short while.");

        var device = await _devices.FindDeviceAsync(session.UserId, request.DeviceId, cancellationToken);
        if (device is null || !device.IsActive)
            throw ApiException.NotFound("device_not_found", "No active device of yours has that id.");

        var signed = BindingContext.Concat(challenge.Nonce).ToArray();
        if (!Ed25519Signatures.Verify(device.SignatureKey, signed, request.Signature ?? []))
            throw ApiException.Unauthorized("invalid_signature", "The signature does not match that device's key.");

        if (!_sessions.BindDevice(session.Token, device.DeviceId))
            throw ApiException.Unauthorized("invalid_session", "The session token is invalid or has expired. Log in again.");

        return new MessageResponse("This session is bound to the device.");
    }

    // ── Key backup ───────────────────────────────────────────────────────────

    /// <summary>Stores the caller's sealed key backup. Needs a bound session.</summary>
    public async Task<KeyBackupResponse> PutBackupAsync(
        UserSession session, PutKeyBackupRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        RequireBound(session);

        if (request.Blob is null || request.Blob.Length == 0)
            throw ApiException.BadRequest("invalid_backup", "A backup is required.");
        if (request.Blob.Length > _options.MaximumBackupBytes)
            throw ApiException.BadRequest("invalid_backup", $"A backup may be at most {_options.MaximumBackupBytes} bytes.");

        var record = new KeyBackupRecord(session.UserId, request.Blob, UtcNow);
        await _backups.PutAsync(record, cancellationToken);
        return new KeyBackupResponse(record.Blob, record.UpdatedAtUtc);
    }

    /// <summary>
    /// The caller's sealed backup. A password session is enough, because a device restoring from the
    /// backup has no key to bind with yet, and the backup is useless without the recovery key.
    /// </summary>
    public async Task<KeyBackupResponse> GetBackupAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        var backup = await _backups.FindAsync(session.UserId, cancellationToken);
        return new KeyBackupResponse(backup?.Blob, backup?.UpdatedAtUtc);
    }

    // ── Linking a new device ─────────────────────────────────────────────────

    /// <summary>A new device (a password session) opens a link for one of its user's devices to approve.</summary>
    public async Task<StartDeviceLinkResponse> StartLinkAsync(
        UserSession session, StartDeviceLinkRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        if (request.Offer is not { Length: > 0 and <= MaximumOfferBytes })
            throw ApiException.BadRequest("invalid_link", $"A link offer is 1 to {MaximumOfferBytes} bytes.");

        var (linkId, expires) = _links.Start(session.UserId, session.Token, request.Offer);
        return new StartDeviceLinkResponse(linkId, expires);
    }

    /// <summary>An existing device (a bound session) fetches the offer to check it against the link code.</summary>
    public async Task<DeviceLinkOfferResponse> GetLinkOfferAsync(
        UserSession session, DeviceLinkRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        RequireBound(session);

        return _links.GetOffer(session.UserId, request.LinkId) is { } offer
            ? new DeviceLinkOfferResponse(offer)
            : throw ApiException.NotFound("link_not_found", "No open link has that code. It may have expired.");
    }

    /// <summary>An existing device (a bound session) answers the link with an approval encrypted to the new device.</summary>
    public async Task<MessageResponse> ApproveLinkAsync(
        UserSession session, ApproveDeviceLinkRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        RequireBound(session);

        if (request.Approval is not { Length: > 0 and <= MaximumApprovalBytes })
            throw ApiException.BadRequest("invalid_link", $"A link approval is 1 to {MaximumApprovalBytes} bytes.");

        return _links.Approve(session.UserId, request.LinkId, request.Approval)
            ? new MessageResponse("The new device can finish linking now.")
            : throw ApiException.NotFound("link_not_found", "No open link has that code. It may have expired.");
    }

    /// <summary>The new device collects the approval, once. Null until an existing device approves.</summary>
    public async Task<DeviceLinkApprovalResponse> TakeLinkApprovalAsync(
        UserSession session, DeviceLinkRequest request, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        return new DeviceLinkApprovalResponse(_links.TakeApproval(session.Token, request.LinkId));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void RequireBound(UserSession session)
    {
        if (session.DeviceId is null)
            throw ApiException.Forbidden("device_required",
                "This needs a session bound to one of your devices. Sign in from a device that has your keys.");
    }

    private async Task<UserRecord> RequireActiveAsync(UserSession session, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(session.UserId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
            throw ApiException.Unauthorized("invalid_session", "This account is no longer a member of the server.");
        return user;
    }

    private async Task NotifyAsync(Func<IDeviceListener, Task> call)
    {
        foreach (var listener in _deviceListeners)
        {
            try
            {
                await call(listener);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[raindrop] {listener.GetType().Name} failed: {ex.Message}");
            }
        }
    }

    private static DeviceSummary ToSummary(DeviceRecord device) =>
        new(device.DeviceId, device.Certificate, device.CreatedAtUtc);
}

/// <summary>Erases a member's end-to-end encryption records when their account is erased.</summary>
internal sealed class ErasedMemberCleanup : IServerEventHandler<MemberEvent>
{
    private readonly DeviceService _devices;
    private readonly IUserStore _users;

    public ErasedMemberCleanup(DeviceService devices, IUserStore users)
    {
        _devices = devices;
        _users = users;
    }

    public async Task HandleAsync(ServerEvent<MemberEvent> serverEvent, CancellationToken cancellationToken)
    {
        if (serverEvent.Type != ServerEventTypes.MemberLeft)
            return;

        // A member who left but preserved their data keeps their keys for a possible return; one whose
        // account was erased has nothing left for them to protect.
        var user = await _users.FindByIdAsync(serverEvent.Data.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Erased)
            await _devices.EraseAsync(serverEvent.Data.UserId, cancellationToken);
    }
}
