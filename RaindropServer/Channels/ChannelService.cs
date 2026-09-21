using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Channels;

/// <summary>
/// The part of channels that is the same whatever kind of channel it is: creating one, listing them,
/// renaming and deleting. Anything that depends on what a channel holds lives in the module for its
/// type — see <see cref="IChannelModule"/> and <c>Channels/Text/</c>.
/// </summary>
public sealed class ChannelService
{
    private readonly IChannelStore _store;
    private readonly IPermissionResolver _permissions;
    private readonly IReadOnlyDictionary<ChannelType, IChannelModule> _modules;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;

    public ChannelService(
        IChannelStore store,
        IPermissionResolver permissions,
        IEnumerable<IChannelModule> modules,
        IEventPublisher events,
        TimeProvider? time = null)
    {
        _store = store;
        _permissions = permissions;
        _modules = modules.ToDictionary(module => module.Type);
        _events = events;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── The channel list ─────────────────────────────────────────────────────

    /// <summary>Lists every channel the caller may see, of every type, ordered by name.</summary>
    public async Task<ChannelListResponse> ListAsync(UserSession session, CancellationToken cancellationToken)
    {
        var channels = await _store.ListAsync(cancellationToken);
        var visible = new List<ChannelSummary>(channels.Count);

        // One resolver call per channel: permissions are allowed to differ channel by channel, and a
        // server with enough channels for that to hurt wants a resolver that caches anyway.
        foreach (var channel in channels)
        {
            var permissions = await _permissions.GetAsync(session.UserId, channel, cancellationToken);

            if (permissions.Allows(Permission.ViewChannel))
                visible.Add(ToSummary(channel, permissions));
        }

        return new ChannelListResponse(visible);
    }

    /// <summary>Returns one channel, as long as the caller may see it.</summary>
    public async Task<ChannelSummary> GetAsync(
        UserSession session, ChannelRequest request, CancellationToken cancellationToken)
    {
        var access = await ResolveAsync(session.UserId, request.ChannelId, null, cancellationToken);
        return ToSummary(access.Channel, access.Permissions);
    }

    // ── The lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a channel of the requested type. A type no <see cref="IChannelModule"/> has been
    /// registered for is refused rather than half-created — which is what stops a channel type from
    /// appearing before the code that runs it does.
    /// </summary>
    public async Task<ChannelSummary> CreateAsync(
        UserSession session, CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var type = ChannelTypeNames.Parse(request.Type);
        var name = ChannelNamePolicy.Validate(request.Name);

        if (!_modules.ContainsKey(type))
            throw ApiException.BadRequest("channel_type_unsupported",
                $"This server cannot host {ChannelTypeNames.Of(type)} channels yet.");

        var permissions = await _permissions.GetAsync(session.UserId, null, cancellationToken);
        permissions.Require(Permission.CreateChannels, "create channels on this server");

        var normalized = ChannelNamePolicy.Normalize(name);
        await RequireNameFreeAsync(normalized, cancellationToken);

        var channel = new ChannelRecord
        {
            ChannelId = Guid.NewGuid(),
            Type = type,
            Name = name,
            NameNormalized = normalized,
            CreatedBy = session.UserId,
            CreatedAtUtc = UtcNow
        };

        await _store.InsertAsync(channel, cancellationToken);
        await PublishChannelAsync(ServerEventTypes.ChannelCreated, channel);

        // Resolved again with the channel in hand: creating it is what makes the caller its moderator.
        var granted = await _permissions.GetAsync(session.UserId, channel, cancellationToken);
        return ToSummary(channel, granted);
    }

    /// <summary>Renames a channel, subject to the same uniqueness rule as creating one.</summary>
    public async Task<ChannelSummary> RenameAsync(
        UserSession session, RenameChannelRequest request, CancellationToken cancellationToken)
    {
        var access = await ResolveAsync(session.UserId, request.ChannelId, null, cancellationToken);
        access.Permissions.Require(Permission.RenameChannels, "rename this channel");

        var name = ChannelNamePolicy.Validate(request.Name);
        var normalized = ChannelNamePolicy.Normalize(name);

        // Re-casing the name it already has is not a claim on a new one.
        if (!string.Equals(normalized, access.Channel.NameNormalized, StringComparison.Ordinal))
            await RequireNameFreeAsync(normalized, cancellationToken);

        var renamed = access.Channel with { Name = name, NameNormalized = normalized };
        await _store.UpdateAsync(renamed, cancellationToken);
        await PublishChannelAsync(ServerEventTypes.ChannelRenamed, renamed);

        return ToSummary(renamed, access.Permissions);
    }

    /// <summary>
    /// Deletes a channel and everything in it. The contents go first, through the module for the
    /// channel's type, so nothing is left pointing at a channel that is no longer there.
    /// </summary>
    public async Task<ChannelDeletedResponse> DeleteAsync(
        UserSession session, ChannelRequest request, CancellationToken cancellationToken)
    {
        var access = await ResolveAsync(session.UserId, request.ChannelId, null, cancellationToken);
        access.Permissions.Require(Permission.DeleteChannels, "delete this channel");

        var deletion = new ChannelDeletion(access.Channel, session.UserId, UtcNow);
        await Module(access.Channel.Type).DeleteContentsAsync(deletion, cancellationToken);
        await _store.DeleteAsync(access.Channel.ChannelId, cancellationToken);

        // Told to whoever could see it, judged against the record still in hand — announcing a
        // channel's id to members it was hidden from would give away that it had existed.
        await _events.PublishAsync(ServerEventTypes.ChannelDeleted,
            new ChannelDeletedEvent(access.Channel.ChannelId), AudienceFor(access.Channel));

        return new ChannelDeletedResponse(access.Channel.ChannelId,
            $"The channel '{access.Channel.Name}' and everything in it have been deleted.");
    }

    // ── For the channel type modules ─────────────────────────────────────────

    /// <summary>
    /// Finds a channel and works out what the caller may do in it — the first thing any channel type's
    /// service does. A channel the caller may not see is reported as missing rather than forbidden,
    /// so the API cannot be used to enumerate channels somebody has been kept out of.
    /// </summary>
    /// <param name="userId">The UUID of the caller.</param>
    /// <param name="channelId">The channel being acted on.</param>
    /// <param name="requiredType">
    /// The type the caller's operation only makes sense for, or null to accept any type.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <exception cref="ApiException">The channel does not exist, is invisible to the caller, or is of another type.</exception>
    public async Task<ChannelAccess> ResolveAsync(
        Guid userId, Guid channelId, ChannelType? requiredType, CancellationToken cancellationToken)
    {
        var channel = channelId == Guid.Empty
            ? null
            : await _store.FindByIdAsync(channelId, cancellationToken);

        if (channel is null)
            throw ChannelNotFound(channelId);

        var permissions = await _permissions.GetAsync(userId, channel, cancellationToken);

        if (!permissions.Allows(Permission.ViewChannel))
            throw ChannelNotFound(channelId);

        if (requiredType is { } required && channel.Type != required)
            throw ApiException.BadRequest("wrong_channel_type",
                $"'{channel.Name}' is a {ChannelTypeNames.Of(channel.Type)} channel, " +
                $"and this only works on a {ChannelTypeNames.Of(required)} channel.");

        return new ChannelAccess(channel, permissions);
    }

    /// <summary>
    /// The members allowed to hear about something in <paramref name="channel"/>: exactly those who
    /// may see it. Every event about a channel or what is in it goes to this audience — including a
    /// plugin's own, which is what keeps a plugin from leaking a hidden channel by accident.
    /// </summary>
    public EventAudience AudienceFor(ChannelRecord channel) => ChannelAudience.For(_permissions, channel);

    /// <summary>Turns a channel and the caller's permissions into the shape sent over the wire.</summary>
    public static ChannelSummary ToSummary(ChannelRecord channel, Permission permissions) => new(
        channel.ChannelId,
        ChannelTypeNames.Of(channel.Type),
        channel.Name,
        channel.CreatedBy,
        channel.CreatedAtUtc,
        PermissionNames.Of(permissions));

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Announces a channel to everyone who may see it, each with their own permissions in it — the
    /// same shape <see cref="ListAsync"/> would have given them. Handlers get it with no permissions,
    /// since they are not anybody in particular.
    /// </summary>
    private Task PublishChannelAsync(string type, ChannelRecord channel) =>
        _events.PublishPerRecipientAsync(type, ToSummary(channel, Permission.None), async (userId, cancellationToken) =>
        {
            var permissions = await _permissions.GetAsync(userId, channel, cancellationToken);
            return permissions.Allows(Permission.ViewChannel) ? ToSummary(channel, permissions) : null;
        });

    private IChannelModule Module(ChannelType type) =>
        _modules.TryGetValue(type, out var module)
            ? module
            : throw new InvalidOperationException(
                $"No module is registered for {ChannelTypeNames.Of(type)} channels, yet one exists in the database.");

    private async Task RequireNameFreeAsync(string normalized, CancellationToken cancellationToken)
    {
        if (await _store.FindByNameAsync(normalized, cancellationToken) is not null)
            throw ApiException.Conflict("channel_name_taken", "A channel with that name already exists on this server.");
    }

    private static ApiException ChannelNotFound(Guid channelId) =>
        ApiException.NotFound("channel_not_found", $"There is no channel {channelId} on this server.");
}

/// <summary>A channel the caller is allowed to see, together with what they may do in it.</summary>
/// <param name="Channel">The channel itself.</param>
/// <param name="Permissions">What the caller may do in it.</param>
public readonly record struct ChannelAccess(ChannelRecord Channel, Permission Permissions);
