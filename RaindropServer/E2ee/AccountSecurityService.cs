using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.E2ee;

/// <param name="LookupId">32 lower-case hex characters the sealed root is fetched by before signing in.</param>
/// <param name="SealedRoot">The root secret sealed under the recovery key.</param>
/// <param name="Vault">The vault sealed under the root.</param>
public sealed record PutIdentityVaultRequest(string? LookupId, byte[]? SealedRoot, byte[]? Vault);

/// <summary>The caller's vault; every field null when they have none.</summary>
public sealed record IdentityVaultResponse(string? LookupId, byte[]? SealedRoot, byte[]? Vault, DateTime? UpdatedAtUtc);

/// <param name="Secret">The TOTP secret.</param>
/// <param name="Code">A current code from it.</param>
/// <param name="BackupCodes">One-time codes for when the authenticator is lost.</param>
public sealed record EnrollAuthenticatorRequest(byte[]? Secret, string? Code, IReadOnlyList<string>? BackupCodes);

public sealed record AuthenticatorStatusResponse(bool Enabled, int BackupCodesLeft);

public sealed record StartPairingResponse(string PairingId, string TakeToken, DateTime ExpiresAtUtc);

/// <summary>
/// What surrounds signing in with a device key: the vault and sealed root a person recovers their
/// identity from, the authenticator that guards adding a device, and the relay a brand-new device is
/// paired through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an authenticator at all.</b> A device joins an account by presenting a certificate signed by
/// the account's identity key, and the recovery words are enough to derive that key. The authenticator
/// is the second thing a device new to the account has to show, so words that leak are not, alone, the
/// account. Its secret is never in the vault — the vault opens with the words — and reaches a paired
/// device only over the pairing channel, which is why pairing needs no code typed: the new device
/// computes one.
/// </para>
/// <para>
/// <b>Getting back in without it.</b> Changing the authenticator needs a session bound to a device
/// and nothing else, so any device still paired replaces a lost one. With no device left, a backup
/// code stands in for a code, once each.
/// </para>
/// </remarks>
public sealed class AccountSecurityService : IDeviceListener
{
    private const int LookupIdLength = 32;
    private const int MaximumSealedRootBytes = 256;
    private const int MaximumOfferBytes = 1024;
    private const int MinimumSecretBytes = 20;
    private const int MaximumSecretBytes = 64;
    private const int MaximumBackupCodes = 10;

    private readonly IIdentityVaultStore _vaults;
    private readonly IAuthenticatorStore _authenticators;
    private readonly IUserStore _users;
    private readonly IPairingRelay _pairings;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    // Wrong codes per account, so six digits cannot be walked through by somebody who holds the
    // recovery words. In memory: a restart forgives, which costs an attacker a restart they cannot cause.
    private readonly ConcurrentDictionary<Guid, (int Failures, DateTime WindowStartedUtc)> _failures = new();

    public AccountSecurityService(
        IIdentityVaultStore vaults,
        IAuthenticatorStore authenticators,
        IUserStore users,
        IPairingRelay pairings,
        E2eeOptions options,
        TimeProvider? time = null)
    {
        _vaults = vaults;
        _authenticators = authenticators;
        _users = users;
        _pairings = pairings;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── The vault ────────────────────────────────────────────────────────────

    /// <summary>Stores the caller's sealed root and vault. Needs a bound session.</summary>
    public async Task<DateTime> PutVaultAsync(
        UserSession session, PutIdentityVaultRequest request, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);

        if (!IsLookupId(request.LookupId))
            throw ApiException.BadRequest("invalid_vault", $"A lookup id is {LookupIdLength} lower-case hex characters.");
        if (request.SealedRoot is not { Length: > 0 and <= MaximumSealedRootBytes })
            throw ApiException.BadRequest("invalid_vault", $"A sealed root is 1 to {MaximumSealedRootBytes} bytes.");
        if (request.Vault is not { Length: > 0 } || request.Vault.Length > _options.MaximumVaultBytes)
            throw ApiException.BadRequest("invalid_vault", $"A vault is 1 to {_options.MaximumVaultBytes} bytes.");

        var record = new IdentityVaultRecord(session.UserId, request.LookupId!, request.SealedRoot, request.Vault, UtcNow);
        if (!await _vaults.PutAsync(record, cancellationToken))
            throw ApiException.Conflict("invalid_vault", "That lookup id cannot be used. Make a new recovery key.");

        return record.UpdatedAtUtc;
    }

    /// <summary>The caller's sealed root and vault. Needs a bound session.</summary>
    public async Task<IdentityVaultResponse> GetVaultAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);
        var vault = await _vaults.FindAsync(session.UserId, cancellationToken);
        return new IdentityVaultResponse(vault?.LookupId, vault?.SealedRoot, vault?.Vault, vault?.UpdatedAtUtc);
    }

    /// <summary>
    /// The sealed root stored under a lookup id, for anybody who asks: the caller is a device with the
    /// recovery words and no account yet. The id is 128 bits derived from the recovery key, so it
    /// cannot be guessed, and what comes back opens only with that key.
    /// </summary>
    public async Task<byte[]> GetSealedRootAsync(string? lookupId, CancellationToken cancellationToken)
    {
        if (IsLookupId(lookupId)
            && await _vaults.FindByLookupAsync(lookupId!, cancellationToken) is { } vault
            && await _users.FindByIdAsync(vault.UserId, cancellationToken) is { Status: UserStatus.Active or UserStatus.LeftDataPreserved })
        {
            return vault.SealedRoot;
        }

        throw ApiException.NotFound("vault_not_found", "Nothing is stored for those recovery words on this server.");
    }

    // ── The authenticator ────────────────────────────────────────────────────

    /// <summary>
    /// Sets or replaces the caller's authenticator. Needs a bound session and a current code from the
    /// new secret — not from the old one, so a device still paired can replace an authenticator that
    /// was lost.
    /// </summary>
    public async Task<MessageResponse> EnrollAuthenticatorAsync(
        UserSession session, EnrollAuthenticatorRequest request, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);

        if (request.Secret is not { Length: >= MinimumSecretBytes and <= MaximumSecretBytes })
            throw ApiException.BadRequest("invalid_authenticator",
                $"An authenticator secret is {MinimumSecretBytes} to {MaximumSecretBytes} bytes.");

        var codes = (request.BackupCodes ?? []).Select(NormalizeBackupCode).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count > MaximumBackupCodes || codes.Any(code => code.Length is < 8 or > 32))
            throw ApiException.BadRequest("invalid_authenticator",
                $"At most {MaximumBackupCodes} backup codes, each 8 to 32 letters and digits.");

        var now = UtcNow;
        if (Totp.MatchingStep(request.Secret, (request.Code ?? "").Trim(), now) is not { } step)
            throw ApiException.Unauthorized("invalid_authenticator_code",
                "That code does not match. Check the authenticator app and this device's clock.");

        // The step just proven is spent: the same code cannot then add a device.
        await _authenticators.PutAsync(
            new AuthenticatorRecord(session.UserId, request.Secret,
                codes.Select(code => HashBackupCode(session.UserId, code)).ToList(), step, now),
            cancellationToken);
        _failures.TryRemove(session.UserId, out _);

        return new MessageResponse("New devices now need a code from your authenticator.");
    }

    /// <summary>Removes the caller's authenticator. Needs a bound session.</summary>
    public async Task<MessageResponse> DisableAuthenticatorAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);
        await _authenticators.DeleteAsync(session.UserId, cancellationToken);
        _failures.TryRemove(session.UserId, out _);
        return new MessageResponse("The authenticator was removed.");
    }

    public async Task<AuthenticatorStatusResponse> GetAuthenticatorStatusAsync(
        UserSession session, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        var authenticator = await _authenticators.FindAsync(session.UserId, cancellationToken);
        return new AuthenticatorStatusResponse(authenticator is not null, authenticator?.BackupCodeHashes.Count ?? 0);
    }

    /// <summary>
    /// Checks the code a device new to <paramref name="userId"/>'s account presented. Returns without
    /// complaint when the account has no authenticator. Called by <see cref="DeviceAuthService"/>
    /// only after the device has shown a certificate signed by the account's identity key, so nobody
    /// else can learn from the answer whether an account has one.
    /// </summary>
    public async Task RequireCodeForNewDeviceAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        var authenticator = await _authenticators.FindAsync(userId, cancellationToken);
        if (authenticator is null)
            return;

        code = (code ?? "").Trim();
        if (code.Length == 0)
            throw ApiException.Unauthorized("authenticator_required",
                "This account needs a code from your authenticator to add a device.");

        var now = UtcNow;
        if (_failures.TryGetValue(userId, out var failures)
            && now - failures.WindowStartedUtc < _options.AuthenticatorLockout
            && failures.Failures >= _options.MaximumAuthenticatorFailures)
        {
            throw ApiException.Unauthorized("authenticator_locked",
                "Too many wrong codes. Wait a while before trying again, or approve the device from one you already use.");
        }

        if (await AcceptAsync(authenticator, code, now, cancellationToken))
        {
            _failures.TryRemove(userId, out _);
            return;
        }

        _failures.AddOrUpdate(userId, (1, now), (_, current) =>
            now - current.WindowStartedUtc < _options.AuthenticatorLockout
                ? (current.Failures + 1, current.WindowStartedUtc)
                : (1, now));

        throw ApiException.Unauthorized("invalid_authenticator_code", "That code is not right, or was already used.");
    }

    private async Task<bool> AcceptAsync(
        AuthenticatorRecord authenticator, string code, DateTime now, CancellationToken cancellationToken)
    {
        // A code is good once: the step it belongs to has to be newer than the last one accepted, and
        // moving that forward is one atomic statement, so two requests cannot both spend it.
        if (Totp.MatchingStep(authenticator.Secret, code, now) is { } step)
            return await _authenticators.TryAdvanceStepAsync(authenticator.UserId, step, cancellationToken);

        var hash = HashBackupCode(authenticator.UserId, NormalizeBackupCode(code));
        if (!authenticator.BackupCodeHashes.Contains(hash, StringComparer.Ordinal))
            return false;

        var remaining = authenticator.BackupCodeHashes.Where(other => other != hash).ToList();
        return await _authenticators.TryReplaceBackupCodesAsync(
            authenticator.UserId, authenticator.BackupCodeHashes, remaining, cancellationToken);
    }

    // ── Pairing a device that has no account yet ─────────────────────────────

    /// <summary>A device with no account posts its pairing offer. Anonymous.</summary>
    public StartPairingResponse StartPairing(byte[]? offer)
    {
        if (offer is not { Length: > 0 and <= MaximumOfferBytes })
            throw ApiException.BadRequest("invalid_pairing", $"A pairing offer is 1 to {MaximumOfferBytes} bytes.");

        return _pairings.Start(offer) is { } started
            ? new StartPairingResponse(started.PairingId, started.TakeToken, started.ExpiresAtUtc)
            : throw ApiException.ServiceUnavailable("pairing_busy", "Too many pairings are in progress. Try again shortly.");
    }

    /// <summary>A paired device (a bound session) fetches the offer to check it against the pairing code.</summary>
    public async Task<byte[]> GetPairingOfferAsync(UserSession session, string? pairingId, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);
        return _pairings.GetOffer(pairingId ?? "")
            ?? throw ApiException.NotFound("pairing_not_found", "No open pairing has that code. It may have expired.");
    }

    /// <summary>A paired device (a bound session) answers with an approval sealed to the new device.</summary>
    public async Task<MessageResponse> ApprovePairingAsync(
        UserSession session, string? pairingId, byte[]? approval, CancellationToken cancellationToken)
    {
        await RequireBoundAsync(session, cancellationToken);

        if (approval is not { Length: > 0 } || approval.Length > _options.MaximumPairingApprovalBytes)
            throw ApiException.BadRequest("invalid_pairing",
                $"A pairing approval is 1 to {_options.MaximumPairingApprovalBytes} bytes.");

        return _pairings.Approve(pairingId ?? "", approval)
            ? new MessageResponse("The new device can finish pairing now.")
            : throw ApiException.NotFound("pairing_not_found", "No open pairing has that code. It may have expired.");
    }

    /// <summary>The new device collects the approval, once. Null until a paired device approves. Anonymous.</summary>
    public byte[]? TakePairingApproval(string? pairingId, string? takeToken) =>
        _pairings.TakeApproval(pairingId ?? "", takeToken ?? "");

    // ── When an identity goes ────────────────────────────────────────────────

    /// <summary>
    /// An identity reset or an erased account: the vault was sealed for an identity that no longer
    /// exists here, and the authenticator guarded it.
    /// </summary>
    public async Task OnIdentityResetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _vaults.DeleteAsync(userId, cancellationToken);
        await _authenticators.DeleteAsync(userId, cancellationToken);
        _failures.TryRemove(userId, out _);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsLookupId(string? lookupId) =>
        lookupId is { Length: LookupIdLength } && lookupId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Backup codes are typed by hand: case, spaces and hyphens are forgiven.</summary>
    private static string NormalizeBackupCode(string? code) =>
        new((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// SHA-256 over the account and the code. Not a slow hash: a backup code is random and long
    /// enough (8 characters at the least) that there is nothing to stretch, and the account in the
    /// hash keeps two accounts' codes from being compared.
    /// </summary>
    private static string HashBackupCode(Guid userId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"raindrop/backup-code/v1\n{userId:D}\n{code}")));

    private async Task RequireBoundAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireActiveAsync(session, cancellationToken);
        if (session.DeviceId is null)
            throw ApiException.Forbidden("device_required",
                "This needs a session bound to one of your devices. Sign in from a device that has your keys.");
    }

    private async Task RequireActiveAsync(UserSession session, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(session.UserId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
            throw ApiException.Unauthorized("invalid_session", "This account is no longer a member of the server.");
    }
}
