namespace RaindropServer.Channels.Voice;

/// <summary>
/// The default <see cref="IVoiceStateStore"/>: one dictionary behind one lock.
/// </summary>
/// <remarks>
/// Correct because a Raindrop server is a single process — the same assumption
/// <c>InMemorySessionStore</c> and <c>EventHub</c> make. A single lock rather than a concurrent
/// dictionary because every operation that matters reads the whole call (to count it) and then
/// changes it; calls are small and changes are rare, so contention is not a concern.
/// </remarks>
public sealed class InMemoryVoiceStateStore : IVoiceStateStore
{
    private readonly Dictionary<Guid, VoiceParticipantRecord> _byUser = new();
    private readonly Lock _gate = new();

    public ValueTask<VoiceJoinOutcome> JoinAsync(
        VoiceParticipantRecord participant, int capacity, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var others = _byUser.Values.Count(record =>
                record.ChannelId == participant.ChannelId && record.UserId != participant.UserId);

            if (others >= capacity)
                return ValueTask.FromResult(new VoiceJoinOutcome(true, null, null, null));

            _byUser.TryGetValue(participant.UserId, out var previous);
            _byUser[participant.UserId] = participant;

            return ValueTask.FromResult(previous is null
                ? new VoiceJoinOutcome(false, null, null, participant)
                : previous.ChannelId == participant.ChannelId
                    ? new VoiceJoinOutcome(false, null, previous, participant)
                    : new VoiceJoinOutcome(false, previous, null, participant));
        }
    }

    public ValueTask<VoiceParticipantRecord?> LeaveAsync(
        Guid userId, Func<VoiceParticipantRecord, bool>? predicate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var record) || predicate?.Invoke(record) == false)
                return ValueTask.FromResult<VoiceParticipantRecord?>(null);

            _byUser.Remove(userId);
            return ValueTask.FromResult<VoiceParticipantRecord?>(record);
        }
    }

    public ValueTask<VoiceParticipantRecord?> UpdateAsync(
        Guid userId,
        Func<VoiceParticipantRecord, VoiceParticipantRecord> update,
        Func<VoiceParticipantRecord, bool>? predicate,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var record) || predicate?.Invoke(record) == false)
                return ValueTask.FromResult<VoiceParticipantRecord?>(null);

            var updated = update(record) with { UserId = record.UserId };
            _byUser[userId] = updated;
            return ValueTask.FromResult<VoiceParticipantRecord?>(updated);
        }
    }

    public ValueTask<VoiceCameraOutcome> SetCameraAsync(
        Guid userId,
        bool on,
        int cameraCapacity,
        Func<VoiceParticipantRecord, bool>? predicate,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var record) || predicate?.Invoke(record) == false)
                return ValueTask.FromResult(new VoiceCameraOutcome(false, false, null));

            // Already what was asked for: nothing to decide, and in particular a member whose camera
            // is on must not be told the call is full because they are counted in it themselves.
            if (record.CameraOn == on)
                return ValueTask.FromResult(new VoiceCameraOutcome(false, false, record));

            if (on)
            {
                if (record.CameraSuppressed)
                    return ValueTask.FromResult(new VoiceCameraOutcome(false, true, null));

                var others = _byUser.Values.Count(other =>
                    other.ChannelId == record.ChannelId && other.UserId != userId && other.CameraOn);

                if (others >= cameraCapacity)
                    return ValueTask.FromResult(new VoiceCameraOutcome(true, false, null));
            }

            var updated = record with { CameraOn = on };
            _byUser[userId] = updated;
            return ValueTask.FromResult(new VoiceCameraOutcome(false, false, updated));
        }
    }

    public ValueTask<VoiceParticipantRecord?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_byUser.GetValueOrDefault(userId));
    }

    public ValueTask<IReadOnlyList<VoiceParticipantRecord>> ListAsync(Guid? channelId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<VoiceParticipantRecord> records = _byUser.Values
                .Where(record => channelId is null || record.ChannelId == channelId)
                .OrderBy(record => record.JoinedAtUtc)
                .ThenBy(record => record.UserId)
                .ToList();

            return ValueTask.FromResult(records);
        }
    }

    public ValueTask<IReadOnlyList<VoiceParticipantRecord>> RemoveChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<VoiceParticipantRecord> removed = _byUser.Values
                .Where(record => record.ChannelId == channelId)
                .OrderBy(record => record.JoinedAtUtc)
                .ToList();

            foreach (var record in removed)
                _byUser.Remove(record.UserId);

            return ValueTask.FromResult(removed);
        }
    }
}
