using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.E2ee;

/// <param name="Nonce">32 random bytes for a device key to sign, usable once.</param>
/// <param name="ExpiresAtUtc">When the challenge lapses.</param>
public sealed record AuthChallengeResponse(byte[] Nonce, DateTime ExpiresAtUtc);

/// <param name="UserId">The account to sign in to.</param>
/// <param name="DeviceId">The device signing in.</param>
/// <param name="Nonce">The nonce <see cref="DeviceAuthService.BeginAsync"/> issued for that account.</param>
/// <param name="Signature">The device key's signature over <see cref="DeviceAuthService.SignedBytes"/>.</param>
/// <param name="Certificate">The device's certificate; needed until the device is registered here.</param>
/// <param name="Username">The username to claim; needed when the UUID has no account yet.</param>
/// <param name="AuthenticatorCode">An authenticator or backup code; needed when the device is new to an account that has an authenticator.</param>
public sealed record CompleteAuthRequest(
    Guid UserId, Guid DeviceId, byte[] Nonce, byte[] Signature, byte[]? Certificate, string? Username,
    string? AuthenticatorCode = null);

/// <summary>
/// Signing in without a password. An account belongs to whoever holds a device key that the account's
/// identity key has certified, so a session is opened by a device signing a one-time challenge, and
/// is bound to that device from the start.
/// </summary>
/// <remarks>
/// <para>
/// One call covers the three things a device can be to this server. A device already registered signs
/// in with its signature alone. A device the account has not seen presents its certificate, which
/// must be signed by the account's identity key — only somebody holding the owner's identity can
/// make one. And a UUID with no account registers: the certificate establishes the identity key and
/// the username is claimed in the same step.
/// </para>
/// <para>
/// The signed bytes include this server's own identity key, so a challenge relayed here from another
/// server (or from here to another) yields a signature that is of no use. Every refusal that would
/// tell a caller whether a UUID or device exists is the same <c>invalid_credentials</c>.
/// </para>
/// <para>
/// A device new to an account that has an authenticator also has to present a code from it (see
/// <see cref="AccountSecurityService"/>). A device already registered never does, and neither does
/// the first device of a new account.
/// </para>
/// </remarks>
public sealed class DeviceAuthService
{
    private static readonly byte[] Context = "raindrop/auth/v1\n"u8.ToArray();
    private const int NonceBytes = 32;

    /// <summary>
    /// Challenges are handed to anybody who asks, so how many may be outstanding is bounded: beyond
    /// this, new ones are refused until some lapse rather than letting an anonymous caller grow the
    /// table without limit.
    /// </summary>
    private const int MaximumOutstandingChallenges = 20_000;

    private readonly IUserStore _users;
    private readonly IDeviceStore _devices;
    private readonly UserService _accounts;
    private readonly DeviceService _deviceService;
    private readonly AccountSecurityService _security;
    private readonly ServerIdentity _identity;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime ExpiresAtUtc)> _challenges =
        new(StringComparer.Ordinal);

    public DeviceAuthService(
        IUserStore users,
        IDeviceStore devices,
        UserService accounts,
        DeviceService deviceService,
        AccountSecurityService security,
        ServerIdentity identity,
        E2eeOptions options,
        TimeProvider? time = null)
    {
        _users = users;
        _devices = devices;
        _accounts = accounts;
        _deviceService = deviceService;
        _security = security;
        _identity = identity;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The exact bytes a device signs: the context, this server's identity key length-prefixed, then
    /// the nonce. Mirrors <c>Session::sign_auth_challenge</c> in the Rust core.
    /// </summary>
    public static byte[] SignedBytes(ReadOnlySpan<byte> serverIdentityKey, ReadOnlySpan<byte> nonce)
    {
        var bytes = new byte[Context.Length + 2 + serverIdentityKey.Length + nonce.Length];
        Context.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(Context.Length), checked((ushort)serverIdentityKey.Length));
        serverIdentityKey.CopyTo(bytes.AsSpan(Context.Length + 2));
        nonce.CopyTo(bytes.AsSpan(Context.Length + 2 + serverIdentityKey.Length));
        return bytes;
    }

    /// <summary>Issues a one-time nonce for one of <paramref name="userId"/>'s devices to sign.</summary>
    public Task<AuthChallengeResponse> BeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
            throw ApiException.BadRequest("invalid_user_id", "A non-empty UUID is required.");
        RequireIdentityKey();

        var now = UtcNow;
        if (_challenges.Count >= MaximumOutstandingChallenges)
        {
            foreach (var (key, challenge) in _challenges)
            {
                if (challenge.ExpiresAtUtc <= now)
                    _challenges.TryRemove(key, out _);
            }

            if (_challenges.Count >= MaximumOutstandingChallenges)
                throw ApiException.ServiceUnavailable("auth_busy", "Too many sign-ins are in progress. Try again shortly.");
        }

        // The same answer whether or not the UUID has an account: asking must not reveal who is here.
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var expires = now + _options.ChallengeLifetime;
        _challenges[Convert.ToHexString(nonce)] = (userId, expires);
        return Task.FromResult(new AuthChallengeResponse(nonce, expires));
    }

    /// <summary>
    /// Opens a session for a device that signed its challenge. The challenge is used up whether or not
    /// the signature is good.
    /// </summary>
    public async Task<SessionResponse> CompleteAsync(CompleteAuthRequest request, CancellationToken cancellationToken)
    {
        var serverKey = RequireIdentityKey();

        if (request.Nonce is not { Length: NonceBytes }
            || !_challenges.TryRemove(Convert.ToHexString(request.Nonce), out var challenge)
            || challenge.UserId != request.UserId
            || challenge.ExpiresAtUtc <= UtcNow)
        {
            throw ApiException.Unauthorized("invalid_challenge", "Ask for a challenge first; it lapses after a short while.");
        }

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken);
        var device = await _devices.FindDeviceAsync(request.UserId, request.DeviceId, cancellationToken);
        var registered = device is { IsActive: true };

        // Which key must have signed: the registered device's, or the one in a certificate that is
        // about exactly this user and device. A revoked device is not helped by presenting its
        // certificate again — registering it below is refused, because its id is taken.
        byte[] signatureKey;
        DeviceCertificate? presented = null;
        if (registered)
        {
            signatureKey = device!.SignatureKey;
        }
        else
        {
            if (request.Certificate is null)
                throw Refused();

            var certificate = DeviceCertificate.Parse(request.Certificate);
            if (certificate.UserId != request.UserId || certificate.DeviceId != request.DeviceId
                || !certificate.IsSignatureValid())
            {
                throw Refused();
            }

            presented = certificate;
            signatureKey = certificate.DeviceSignatureKey;
        }

        if (!Ed25519Signatures.Verify(signatureKey, SignedBytes(serverKey, request.Nonce), request.Signature ?? []))
            throw Refused();

        // From here on the caller holds the device key. What is left is whether that device is the
        // account owner's, which the certificate's signer answers.
        if (user is null || user.Status == UserStatus.Erased)
        {
            if (registered)
                throw Refused();

            user = await _accounts.RegisterForDeviceAsync(request.UserId, request.Username, cancellationToken);
            await _deviceService.RegisterCertificateAsync(user.UserId, request.Certificate, cancellationToken);
        }
        else
        {
            if (!registered)
            {
                // An account with no identity key was made with a password and has never had a
                // device. Letting a certificate establish its identity here would hand the account
                // to whoever asked first.
                var identity = await _devices.FindIdentityAsync(user.UserId, cancellationToken);
                if (identity is null)
                    throw Refused();

                // Checked here, before the authenticator, so that only somebody holding the
                // account's identity key ever gets to try a code — or to learn there is one to try.
                if (!CryptographicOperations.FixedTimeEquals(identity.IdentityKey, presented!.UserIdentityKey))
                    throw ApiException.Conflict("identity_mismatch",
                        "That certificate was signed by a different identity key than this account's.");

                await _security.RequireCodeForNewDeviceAsync(user.UserId, request.AuthenticatorCode, cancellationToken);
                await _deviceService.RegisterCertificateAsync(user.UserId, request.Certificate, cancellationToken);
            }

            user = await _accounts.RejoinForDeviceAsync(user, cancellationToken);
        }

        return await _accounts.OpenDeviceSessionAsync(user, request.DeviceId, cancellationToken);
    }

    private byte[] RequireIdentityKey() =>
        _identity.IdentityKey
        ?? throw ApiException.ServiceUnavailable("auth_unavailable",
            "This server has no identity key, so devices cannot sign in to it.");

    private static ApiException Refused() =>
        ApiException.Unauthorized("invalid_credentials", "This device could not be signed in to that account.");
}
