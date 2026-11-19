using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RaindropServer.Users;

namespace RaindropServer.E2ee;

/// <param name="UserId">The account the code hands over.</param>
/// <param name="Username">Its username, for the owner to check they picked the right one.</param>
/// <param name="Code">The claim code to give the person: <c>RDC1-&lt;user id&gt;-&lt;secret&gt;</c>.</param>
/// <param name="ExpiresAtUtc">When it stops working.</param>
public sealed record AccountClaim(Guid UserId, string Username, string Code, DateTime ExpiresAtUtc);

/// <summary>
/// The last resort when a person has lost everything — every device, the recovery words and any
/// backup — so no identity of theirs can prove the account is theirs any more. The server's owner,
/// who has to be satisfied it really is them (in person, by a channel they trust), issues a claim
/// code from the console; a device of the person's new identity redeems it in
/// <see cref="DeviceAuthService.CompleteAsync"/> and takes the account over: same id, username,
/// roles and authorship. Their old identity is reset as <see cref="DeviceService.ResetIdentityAsync"/>
/// resets it, so members see the safety number change, and old encrypted history stays unreadable.
/// </summary>
/// <remarks>
/// <para>
/// This gives the owner nothing they did not have: whoever runs the server can rewrite its database.
/// What it adds is a way to do it that is visible — every member is sent <c>e2ee.identity.reset</c> —
/// rather than a hand-edited row.
/// </para>
/// <para>
/// Codes are kept in memory, hashed, one per account, for <see cref="E2eeOptions.ClaimLifetime"/>: a
/// restart forgets them, and the owner issues another. Nothing about the account changes until a code
/// is redeemed, so one issued by mistake costs nothing.
/// </para>
/// </remarks>
public sealed class AccountClaimService
{
    private const string Prefix = "RDC1-";
    private const int SecretBytes = 16;

    private readonly IUserStore _users;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, (byte[] Hash, DateTime ExpiresAtUtc)> _claims = new();

    public AccountClaimService(IUserStore users, E2eeOptions options, TimeProvider? time = null)
    {
        _users = users;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Issues a claim code for <paramref name="username"/>'s account, replacing any earlier one. The
    /// account must be active, or have left with its data preserved.
    /// </summary>
    public async Task<AccountClaim> IssueAsync(string username, CancellationToken cancellationToken)
    {
        var user = await _users.FindByUsernameAsync(username.Trim().ToLowerInvariant(), cancellationToken);
        if (user is not { Status: UserStatus.Active or UserStatus.LeftDataPreserved, Username: { } name })
            throw ApiException.NotFound("user_not_found", $"No member here is called '{username}'.");

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretBytes)).ToLowerInvariant();
        var expires = UtcNow + _options.ClaimLifetime;
        _claims[user.UserId] = (Hash(user.UserId, secret), expires);
        return new AccountClaim(user.UserId, name, $"{Prefix}{user.UserId:D}-{secret}", expires);
    }

    /// <summary>
    /// Spends <paramref name="code"/> if it is the live one for <paramref name="userId"/>. True at most
    /// once per code, however many requests present it at the same moment.
    /// </summary>
    public bool TryRedeem(Guid userId, string? code)
    {
        if (!TryParse(code, out var claimed, out var secret) || claimed != userId)
            return false;
        if (!_claims.TryGetValue(userId, out var claim) || claim.ExpiresAtUtc <= UtcNow)
            return false;
        if (!CryptographicOperations.FixedTimeEquals(claim.Hash, Hash(userId, secret)))
            return false;

        // Removed only if it is still exactly the claim checked, so two redemptions cannot both win.
        return _claims.TryRemove(new KeyValuePair<Guid, (byte[], DateTime)>(userId, claim));
    }

    /// <summary>Reads <c>RDC1-&lt;user id&gt;-&lt;32 hex&gt;</c>, forgiving case and surrounding space.</summary>
    public static bool TryParse(string? code, out Guid userId, out string secret)
    {
        userId = Guid.Empty;
        secret = "";
        var text = (code ?? "").Trim().ToLowerInvariant();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || text.Length != Prefix.Length + 36 + 1 + SecretBytes * 2)
            return false;

        var id = text.Substring(Prefix.Length, 36);
        if (text[Prefix.Length + 36] != '-' || !Guid.TryParseExact(id, "D", out userId))
            return false;

        secret = text[(Prefix.Length + 37)..];
        return secret.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static byte[] Hash(Guid userId, string secret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"raindrop/claim/v1\n{userId:D}\n{secret}"));
}
