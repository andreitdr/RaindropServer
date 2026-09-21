using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Every rule about voice calls: who may join, speak and moderate, who is in which call, and keeping
/// the media server in step with those decisions. Endpoints stay thin and the transport stays dumb —
/// the same division <see cref="Text.TextChannelService"/> follows.
/// </summary>
/// <remarks>
/// Raindrop is the authority and the media server only carries audio. Two things keep them
/// consistent:
/// <list type="bullet">
/// <item>
/// Decisions go to the media server as grants: a member's credential says whether their microphone is
/// accepted, and a change mid-call is pushed with <see cref="IVoiceTransport.SetCanSpeakAsync"/>.
/// </item>
/// <item>
/// <see cref="ReconcileAsync"/> compares the two records on a timer, both ways. A participant Raindrop
/// admitted but the media server never saw (or stopped seeing) is dropped; one the media server has
/// but Raindrop did not admit is removed. The second matters because a self-hosted media server does
/// not revoke a credential when its holder is removed — only its short lifetime and this sweep do.
/// </item>
/// </list>
/// Work that touches the media server outside a request goes through <see cref="VoiceWorkQueue"/>,
/// so a slow or absent media server never holds up a request.
/// </remarks>
public sealed class VoiceService
{
    private readonly ChannelService _channels;
    private readonly IChannelStore _channelStore;
    private readonly IPermissionResolver _permissions;
    private readonly IVoiceStateStore _state;
    private readonly IVoiceMuteStore _mutes;
    private readonly IVoiceTransport _transport;
    private readonly IEventPublisher _events;
    private readonly ISessionStore _sessions;
    private readonly IUserStore _users;
    private readonly VoiceWorkQueue _work;
    private readonly VoiceOptions _options;
    private readonly IReadOnlyList<IVoiceJoinPolicy> _policies;
    private readonly TimeProvider _time;

    public VoiceService(
        ChannelService channels,
        IChannelStore channelStore,
        IPermissionResolver permissions,
        IVoiceStateStore state,
        IVoiceMuteStore mutes,
        IVoiceTransport transport,
        IEventPublisher events,
        ISessionStore sessions,
        IUserStore users,
        VoiceWorkQueue work,
        VoiceOptions options,
        IEnumerable<IVoiceJoinPolicy> policies,
        TimeProvider? time = null)
    {
        _channels = channels;
        _channelStore = channelStore;
        _permissions = permissions;
        _state = state;
        _mutes = mutes;
        _transport = transport;
        _events = events;
        _sessions = sessions;
        _users = users;
        _work = work;
        _options = options;
        _policies = policies.ToList();
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>The transport clients will be told to connect with.</summary>
    public IVoiceTransport Transport => _transport;

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether voice works here, and who is in every call in a channel the caller may see — what a
    /// channel list needs to draw the people under each voice channel.
    /// </summary>
    public async Task<VoiceStatusResponse> StatusAsync(UserSession session, CancellationToken cancellationToken)
    {
        var records = await _state.ListAsync(null, cancellationToken);
        var visible = new List<VoiceParticipant>(records.Count);

        // One resolver call per channel with somebody in it, not per participant.
        foreach (var call in records.GroupBy(record => record.ChannelId))
        {
            var channel = await _channelStore.FindByIdAsync(call.Key, cancellationToken);
            if (channel is null)
                continue;

            var permissions = await _permissions.GetAsync(session.UserId, channel, cancellationToken);
            if (permissions.Allows(Permission.ViewChannel))
                visible.AddRange(call.Select(VoiceEvents.ToContract));
        }

        return new VoiceStatusResponse(
            _transport.IsAvailable,
            _transport.Name,
            _options.MaximumParticipantsPerChannel,
            visible);
    }

    // ── Joining and leaving ──────────────────────────────────────────────────

    /// <summary>
    /// Admits the caller to a voice channel's call and returns what their client needs to connect.
    /// Joining a second call leaves the first; joining the same call from another device takes it over.
    /// </summary>
    public async Task<JoinVoiceResponse> JoinAsync(
        UserSession session, JoinVoiceRequest request, CancellationToken cancellationToken)
    {
        if (!_transport.IsAvailable)
            throw Unavailable();

        var access = await _channels.ResolveAsync(session.UserId, request.ChannelId, ChannelType.Voice, cancellationToken);
        access.Permissions.Require(Permission.Connect, "join voice channels");

        foreach (var policy in _policies)
            await policy.CheckAsync(new VoiceJoinContext(session, access.Channel, access.Permissions), cancellationToken);

        var now = UtcNow;
        var record = new VoiceParticipantRecord
        {
            ChannelId = access.Channel.ChannelId,
            UserId = session.UserId,
            SessionToken = session.Token,
            SelfMute = request.SelfMute || request.SelfDeaf,
            SelfDeaf = request.SelfDeaf,
            ServerMute = await _mutes.FindAsync(session.UserId, cancellationToken) is not null,
            Suppressed = !access.Permissions.Allows(Permission.Speak),
            Connected = false,
            JoinedAtUtc = now,
            LastSeenUtc = now
        };

        // Minted before admitting, so a transport that refuses leaves nothing behind to clean up.
        var connection = await _transport.CreateConnectionAsync(
            new VoiceGrant(record.ChannelId, record.UserId, session.Username, record.CanSpeak), cancellationToken);

        var outcome = await _state.JoinAsync(record, _options.MaximumParticipantsPerChannel, cancellationToken);

        if (outcome.Full)
            throw ApiException.Conflict("channel_full",
                $"'{access.Channel.Name}' is full: a call holds at most {_options.MaximumParticipantsPerChannel} members.");

        if (outcome.MovedFrom is { } previous)
        {
            var previousChannel = await _channelStore.FindByIdAsync(previous.ChannelId, cancellationToken);
            await VoiceEvents.PublishLeftAsync(_events, _permissions, previousChannel, previous, VoiceLeaveReasons.Moved);
            EnqueueDisconnect(previous.ChannelId, previous.UserId);
        }

        // A takeover from another device needs no voice.left: the member is still in the call, and the
        // media server drops the older connection itself once the new one arrives.
        await VoiceEvents.PublishStateAsync(_events, _permissions, access.Channel, record);

        var participants = await _state.ListAsync(record.ChannelId, cancellationToken);

        return new JoinVoiceResponse(
            record.ChannelId,
            connection.Transport,
            connection.Url,
            connection.Path,
            connection.Token,
            connection.ExpiresAtUtc,
            VoiceEvents.ToContract(record),
            participants.Select(VoiceEvents.ToContract).ToList());
    }

    /// <summary>
    /// Takes the caller out of a call. Only the session that joined can leave on its own behalf, so a
    /// stale request from a device the call has since moved to another is a no-op, not an ending.
    /// </summary>
    public async Task<LeaveVoiceResponse> LeaveAsync(
        UserSession session, LeaveVoiceRequest request, CancellationToken cancellationToken)
    {
        var removed = await _state.LeaveAsync(session.UserId,
            record => record.ChannelId == request.ChannelId && record.SessionToken == session.Token,
            cancellationToken);

        if (removed is null)
            return new LeaveVoiceResponse(false);

        await AnnounceRemovedAsync(removed, VoiceLeaveReasons.Left, cancellationToken);
        return new LeaveVoiceResponse(true);
    }

    /// <summary>Mutes or deafens the caller in the call they are in. Deafening always mutes too.</summary>
    public async Task<VoiceParticipant> UpdateSelfStateAsync(
        UserSession session, VoiceSelfStateRequest request, CancellationToken cancellationToken)
    {
        var updated = await _state.UpdateAsync(session.UserId,
            record => record with { SelfMute = request.SelfMute || request.SelfDeaf, SelfDeaf = request.SelfDeaf },
            record => record.SessionToken == session.Token,
            cancellationToken);

        if (updated is null)
            throw ApiException.Conflict("not_in_voice", "You are not in a voice call from this session.");

        var channel = await _channelStore.FindByIdAsync(updated.ChannelId, cancellationToken);
        await VoiceEvents.PublishStateAsync(_events, _permissions, channel, updated);

        return VoiceEvents.ToContract(updated);
    }

    // ── Moderation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Mutes a member in every call on this server, for everyone, or lifts that mute. Needs
    /// <see cref="Permission.MuteMembers"/> server-wide, since the mute follows its target into every
    /// call; works whether or not they are in one right now.
    /// </summary>
    /// <remarks>
    /// A member holding <see cref="Permission.Administrator"/> can only be muted by another
    /// administrator — the same spirit as "nobody may hand out what they do not hold" in
    /// <see cref="RoleService"/>. Nothing stops an administrator unmuting themselves, so this cannot
    /// lock anybody out.
    /// </remarks>
    public async Task<VoiceMuteResponse> SetServerMuteAsync(
        UserSession session, VoiceMuteRequest request, CancellationToken cancellationToken)
    {
        var granted = await _permissions.GetAsync(session.UserId, null, cancellationToken);
        granted.Require(Permission.MuteMembers, "mute members for everyone");

        var target = request.UserId == Guid.Empty
            ? null
            : await _users.FindByIdAsync(request.UserId, cancellationToken);

        if (target is null || target.Status != UserStatus.Active)
            throw ApiException.NotFound("member_not_found", $"There is no member {request.UserId} on this server.");

        if (request.Muted)
        {
            var theirs = await _permissions.GetAsync(target.UserId, null, cancellationToken);
            if (theirs.HasFlag(Permission.Administrator) && !granted.HasFlag(Permission.Administrator))
                throw ApiException.Forbidden("missing_permission",
                    "Only an administrator can mute an administrator for everyone.");
        }

        var changed = request.Muted
            ? await _mutes.AddAsync(new VoiceMuteRecord(target.UserId, session.UserId, UtcNow), cancellationToken)
            : await _mutes.RemoveAsync(target.UserId, cancellationToken);

        var updated = await _state.UpdateAsync(target.UserId,
            record => record with { ServerMute = request.Muted },
            record => record.ServerMute != request.Muted,
            cancellationToken);

        if (updated is not null)
        {
            EnqueueSpeakSync(updated.UserId);

            var channel = await _channelStore.FindByIdAsync(updated.ChannelId, cancellationToken);
            await VoiceEvents.PublishStateAsync(_events, _permissions, channel, updated);
        }

        return new VoiceMuteResponse(target.UserId, request.Muted, changed);
    }

    // ── Maintenance (driven by VoiceMaintenanceService) ──────────────────────

    /// <summary>
    /// Re-checks participants against what they are allowed now: still able to see the channel, still
    /// holding <c>connect</c>, still logged in on the session that joined, and whether they may speak.
    /// Everybody when <paramref name="userId"/> is null, as after a role change; one member after
    /// their sessions change.
    /// </summary>
    public async Task ReevaluateAsync(Guid? userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<VoiceParticipantRecord> records;
        if (userId is { } one)
            records = await _state.FindAsync(one, cancellationToken) is { } found ? [found] : [];
        else
            records = await _state.ListAsync(null, cancellationToken);

        foreach (var record in records)
        {
            if (!_sessions.IsActive(record.SessionToken))
            {
                await RemoveAsync(record, VoiceLeaveReasons.SessionEnded, cancellationToken);
                continue;
            }

            var channel = await _channelStore.FindByIdAsync(record.ChannelId, cancellationToken);
            if (channel is null)
            {
                await RemoveAsync(record, VoiceLeaveReasons.ChannelDeleted, cancellationToken);
                continue;
            }

            var permissions = await _permissions.GetAsync(record.UserId, channel, cancellationToken);
            if (!permissions.Allows(Permission.ViewChannel | Permission.Connect))
            {
                await RemoveAsync(record, VoiceLeaveReasons.PermissionLost, cancellationToken);
                continue;
            }

            var suppressed = !permissions.Allows(Permission.Speak);
            if (suppressed == record.Suppressed)
                continue;

            var updated = await _state.UpdateAsync(record.UserId,
                current => current with { Suppressed = suppressed },
                current => current.SessionToken == record.SessionToken,
                cancellationToken);

            if (updated is null)
                continue;

            await SyncSpeakAsync(updated, cancellationToken);
            await VoiceEvents.PublishStateAsync(_events, _permissions, channel, updated);
        }
    }

    /// <summary>
    /// Compares Raindrop's record of every call with the media server's, and settles every difference
    /// in Raindrop's favour. Does nothing when the media server cannot be reached: the next pass will.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        // Sessions first, and whatever the media server's state: a member who logged out is out of
        // the call even while the media server cannot be asked about it.
        foreach (var record in await _state.ListAsync(null, cancellationToken))
        {
            if (!_sessions.IsActive(record.SessionToken))
                await RemoveAsync(record, VoiceLeaveReasons.SessionEnded, cancellationToken);
        }

        if (!_transport.IsAvailable)
            return;

        var calls = await _transport.ListCallsAsync(cancellationToken);
        var now = UtcNow;

        var present = new Dictionary<(Guid ChannelId, Guid UserId), TransportParticipant>();
        foreach (var call in calls)
        {
            foreach (var participant in call.Participants)
                present.TryAdd((call.ChannelId, participant.UserId), participant);
        }

        var records = await _state.ListAsync(null, cancellationToken);
        var admitted = new HashSet<(Guid ChannelId, Guid UserId)>();

        foreach (var record in records)
        {
            if (!present.TryGetValue((record.ChannelId, record.UserId), out var seen))
            {
                // Not in the call as far as the media server knows: still connecting, briefly gone
                // while reconnecting, or gone for good. Only the last outlasts the grace period.
                if (now - record.LastSeenUtc > _options.JoinGrace)
                    await RemoveAsync(record, VoiceLeaveReasons.Disconnected, cancellationToken);
                else
                    admitted.Add((record.ChannelId, record.UserId));

                continue;
            }

            admitted.Add((record.ChannelId, record.UserId));

            var updated = await _state.UpdateAsync(record.UserId,
                current => current with { Connected = true, LastSeenUtc = now },
                current => current.SessionToken == record.SessionToken && current.ChannelId == record.ChannelId,
                cancellationToken);

            if (updated is null)
                continue;

            if (seen.CanSpeak != updated.CanSpeak)
                await SyncSpeakAsync(updated, cancellationToken);

            if (!record.Connected)
            {
                var channel = await _channelStore.FindByIdAsync(updated.ChannelId, cancellationToken);
                await VoiceEvents.PublishStateAsync(_events, _permissions, channel, updated);
            }
        }

        // Anybody the media server has that Raindrop did not admit — a removed member reconnecting on a
        // credential that has not expired yet, or a call that outlived a restart — goes.
        foreach (var call in calls)
        {
            if (await _channelStore.FindByIdAsync(call.ChannelId, cancellationToken) is null)
            {
                await TryTransportAsync(ct => _transport.CloseCallAsync(call.ChannelId, ct), cancellationToken);
                continue;
            }

            foreach (var participant in call.Participants)
            {
                if (!admitted.Contains((call.ChannelId, participant.UserId)))
                    await TryTransportAsync(ct => _transport.DisconnectAsync(call.ChannelId, participant.UserId, ct),
                        cancellationToken);
            }
        }
    }

    /// <summary>
    /// Ends every call — at startup, where no call can be adopted because every session died with the
    /// previous process, and at shutdown. Best-effort against the media server.
    /// </summary>
    public async Task EndAllCallsAsync(CancellationToken cancellationToken)
    {
        var records = await _state.ListAsync(null, cancellationToken);

        foreach (var channelId in records.Select(record => record.ChannelId).Distinct())
        {
            var channel = await _channelStore.FindByIdAsync(channelId, cancellationToken);

            foreach (var removed in await _state.RemoveChannelAsync(channelId, cancellationToken))
                await VoiceEvents.PublishLeftAsync(_events, _permissions, channel, removed, VoiceLeaveReasons.ServerEnded);
        }

        if (!_transport.IsAvailable)
            return;

        var calls = await _transport.ListCallsAsync(cancellationToken);
        await Task.WhenAll(calls.Select(call =>
            TryTransportAsync(ct => _transport.CloseCallAsync(call.ChannelId, ct), cancellationToken)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The error every join gets on a server with no media server configured.</summary>
    public static ApiException Unavailable() =>
        ApiException.ServiceUnavailable("voice_unavailable",
            "Voice calls are not set up on this server: it has no media server configured.");

    /// <summary>
    /// Takes one participant out — as long as the record is still the one decided on — and tells
    /// everybody, and the media server.
    /// </summary>
    private async Task RemoveAsync(VoiceParticipantRecord record, string reason, CancellationToken cancellationToken)
    {
        var removed = await _state.LeaveAsync(record.UserId,
            current => current.ChannelId == record.ChannelId && current.SessionToken == record.SessionToken,
            cancellationToken);

        if (removed is not null)
            await AnnounceRemovedAsync(removed, reason, cancellationToken);
    }

    private async Task AnnounceRemovedAsync(VoiceParticipantRecord removed, string reason, CancellationToken cancellationToken)
    {
        var channel = await _channelStore.FindByIdAsync(removed.ChannelId, cancellationToken);
        await VoiceEvents.PublishLeftAsync(_events, _permissions, channel, removed, reason);
        EnqueueDisconnect(removed.ChannelId, removed.UserId);
    }

    /// <summary>
    /// Drops a member's media connection, unless by the time the work runs they are back in that
    /// call — rejoining quickly after leaving must not get the new connection removed.
    /// </summary>
    private void EnqueueDisconnect(Guid channelId, Guid userId) =>
        _work.Enqueue(async cancellationToken =>
        {
            if (await _state.FindAsync(userId, cancellationToken) is { } current && current.ChannelId == channelId)
                return;

            await TryTransportAsync(ct => _transport.DisconnectAsync(channelId, userId, ct), cancellationToken);
        });

    /// <summary>Pushes whatever a member's record now says about speaking to the media server.</summary>
    private void EnqueueSpeakSync(Guid userId) =>
        _work.Enqueue(async cancellationToken =>
        {
            if (await _state.FindAsync(userId, cancellationToken) is { } current)
                await SyncSpeakAsync(current, cancellationToken);
        });

    private Task SyncSpeakAsync(VoiceParticipantRecord record, CancellationToken cancellationToken) =>
        TryTransportAsync(ct => _transport.SetCanSpeakAsync(record.ChannelId, record.UserId, record.CanSpeak, ct),
            cancellationToken);

    /// <summary>
    /// Runs a media-server call whose failure is not the caller's problem: the reconciler notices
    /// whatever it left undone and does it again.
    /// </summary>
    private static async Task TryTransportAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        try
        {
            await call(cancellationToken);
        }
        catch (VoiceTransportException ex)
        {
            Console.Error.WriteLine($"[raindrop] voice: {ex.Message}");
        }
    }
}
