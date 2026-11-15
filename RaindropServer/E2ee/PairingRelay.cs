using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RaindropServer.E2ee;

/// <summary>
/// Carries a pairing handshake between a device that has no account anywhere yet and one of the same
/// person's paired devices. Unlike <see cref="IDeviceLinkRelay"/> the new device is anonymous: it
/// does not know its account until the approval gives it the person's root secret.
/// </summary>
/// <remarks>
/// Everything carried is opaque. The pairing code the person carries across commits to the offer, so
/// a substituted offer is noticed, and the approval is sealed to the new device. At worst the relay —
/// or another member who guessed a pairing id — can stop a pairing from completing.
/// </remarks>
public interface IPairingRelay
{
    /// <summary>Opens a pairing, or returns null when too many are open already.</summary>
    (string PairingId, string TakeToken, DateTime ExpiresAtUtc)? Start(byte[] offer);

    byte[]? GetOffer(string pairingId);

    /// <summary>Records an approval. Returns false when the pairing is gone or already approved.</summary>
    bool Approve(string pairingId, byte[] approval);

    /// <summary>
    /// Hands the approval to the holder of <paramref name="takeToken"/>, once, closing the pairing.
    /// Null while nobody has approved it yet, and for a wrong token.
    /// </summary>
    byte[]? TakeApproval(string pairingId, string takeToken);
}

/// <summary>The default <see cref="IPairingRelay"/>: in memory, like sessions, and swept lazily.</summary>
public sealed class InMemoryPairingRelay : IPairingRelay
{
    // Unambiguous characters only: a pairing id may be typed in by hand.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int IdLength = 10;

    private readonly ConcurrentDictionary<string, Pairing> _pairings = new(StringComparer.Ordinal);
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    public InMemoryPairingRelay(E2eeOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public (string PairingId, string TakeToken, DateTime ExpiresAtUtc)? Start(byte[] offer)
    {
        var now = UtcNow;
        foreach (var (id, pairing) in _pairings)
        {
            if (pairing.ExpiresAtUtc <= now)
                _pairings.TryRemove(id, out _);
        }

        if (_pairings.Count >= _options.MaximumOpenPairings)
            return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = now + _options.DeviceLinkLifetime;
        while (true)
        {
            var id = new string(RandomNumberGenerator.GetItems<char>(Alphabet, IdLength));
            if (_pairings.TryAdd(id, new Pairing(token, offer, null, expires)))
                return (id, token, expires);
        }
    }

    public byte[]? GetOffer(string pairingId) => Find(Normalize(pairingId))?.Offer;

    public bool Approve(string pairingId, byte[] approval)
    {
        pairingId = Normalize(pairingId);
        if (Find(pairingId) is not { Approval: null } pairing)
            return false;

        return _pairings.TryUpdate(pairingId, pairing with { Approval = approval }, pairing);
    }

    public byte[]? TakeApproval(string pairingId, string takeToken)
    {
        pairingId = Normalize(pairingId);
        if (Find(pairingId) is not { } pairing
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(pairing.TakeToken), System.Text.Encoding.UTF8.GetBytes(takeToken ?? ""))
            || pairing.Approval is null)
        {
            return null;
        }

        return _pairings.TryRemove(new KeyValuePair<string, Pairing>(pairingId, pairing)) ? pairing.Approval : null;
    }

    /// <summary>Pairing ids may be typed in by hand, so case and stray spaces are forgiven.</summary>
    private static string Normalize(string? pairingId) => (pairingId ?? "").Trim().ToUpperInvariant();

    private Pairing? Find(string pairingId)
    {
        if (pairingId.Length == 0 || !_pairings.TryGetValue(pairingId, out var pairing))
            return null;

        if (pairing.ExpiresAtUtc > UtcNow)
            return pairing;

        _pairings.TryRemove(pairingId, out _);
        return null;
    }

    private sealed record Pairing(string TakeToken, byte[] Offer, byte[]? Approval, DateTime ExpiresAtUtc);
}
