using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RaindropServer.E2ee;

/// <summary>
/// Carries a device-link handshake between a new device and one of the same user's existing
/// devices, which usually cannot reach each other directly.
/// </summary>
/// <remarks>
/// Everything it carries is opaque. The new device's offer commits to its keys in the link code the
/// user carries across (a QR code, or text), so the existing device notices a substituted offer. The
/// approval is encrypted to the new device. At worst, the relay can refuse to deliver.
/// </remarks>
public interface IDeviceLinkRelay
{
    /// <summary>Opens a link owned by <paramref name="ownerToken"/>'s session.</summary>
    (string LinkId, DateTime ExpiresAtUtc) Start(Guid userId, string ownerToken, byte[] offer);

    /// <summary>The offer of one of <paramref name="userId"/>'s open links, or null.</summary>
    byte[]? GetOffer(Guid userId, string linkId);

    /// <summary>Records an approval. Returns false when the link is gone or already approved.</summary>
    bool Approve(Guid userId, string linkId, byte[] approval);

    /// <summary>
    /// Hands the approval to the session that opened the link, once, closing the link. Null while
    /// nobody has approved it yet.
    /// </summary>
    byte[]? TakeApproval(string ownerToken, string linkId);
}

/// <summary>The default <see cref="IDeviceLinkRelay"/>: in memory, like sessions, and swept lazily.</summary>
public sealed class InMemoryDeviceLinkRelay : IDeviceLinkRelay
{
    // Unambiguous characters only: a link id may be typed in by hand.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int IdLength = 10;

    private readonly ConcurrentDictionary<string, Link> _links = new(StringComparer.Ordinal);
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    public InMemoryDeviceLinkRelay(E2eeOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public (string LinkId, DateTime ExpiresAtUtc) Start(Guid userId, string ownerToken, byte[] offer)
    {
        Sweep();

        var expires = UtcNow + _options.DeviceLinkLifetime;
        while (true)
        {
            var id = new string(RandomNumberGenerator.GetItems<char>(Alphabet, IdLength));
            if (_links.TryAdd(id, new Link(userId, ownerToken, offer, null, expires)))
                return (id, expires);
        }
    }

    public byte[]? GetOffer(Guid userId, string linkId) =>
        Find(Normalize(linkId)) is { } link && link.UserId == userId ? link.Offer : null;

    public bool Approve(Guid userId, string linkId, byte[] approval)
    {
        linkId = Normalize(linkId);
        if (Find(linkId) is not { } link || link.UserId != userId || link.Approval is not null)
            return false;

        return _links.TryUpdate(linkId, link with { Approval = approval }, link);
    }

    public byte[]? TakeApproval(string ownerToken, string linkId)
    {
        linkId = Normalize(linkId);
        if (Find(linkId) is not { } link || link.OwnerToken != ownerToken || link.Approval is null)
            return null;

        return _links.TryRemove(new KeyValuePair<string, Link>(linkId, link)) ? link.Approval : null;
    }

    /// <summary>Link ids may be typed in by hand, so case and stray spaces are forgiven.</summary>
    private static string Normalize(string? linkId) => (linkId ?? "").Trim().ToUpperInvariant();

    private Link? Find(string linkId)
    {
        if (linkId.Length == 0 || !_links.TryGetValue(linkId, out var link))
            return null;

        if (link.ExpiresAtUtc > UtcNow)
            return link;

        _links.TryRemove(linkId, out _);
        return null;
    }

    private void Sweep()
    {
        var now = UtcNow;
        foreach (var (id, link) in _links)
        {
            if (link.ExpiresAtUtc <= now)
                _links.TryRemove(id, out _);
        }
    }

    private sealed record Link(Guid UserId, string OwnerToken, byte[] Offer, byte[]? Approval, DateTime ExpiresAtUtc);
}
