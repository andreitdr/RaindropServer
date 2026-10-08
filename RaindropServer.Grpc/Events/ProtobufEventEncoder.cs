using System.Collections.Concurrent;
using Google.Protobuf;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.E2ee;
using RaindropServer.Events;
using RaindropServer.Grpc.Mapping;
using RaindropServer.Roles;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Events;

/// <summary>
/// Turns each event into one <c>ConnectResponse</c> for the gRPC stream: a built-in event type becomes
/// its own case of <c>ServerEvent</c>, and anything else is a plugin's, carried as <c>PluginEvent</c> —
/// which needs its payload to be a protobuf message, since that is the only encoding the stream has.
/// </summary>
public sealed class ProtobufEventEncoder : IEventEncoder
{
    private readonly ConcurrentDictionary<string, bool> _unencodable = new();

    /// <inheritdoc />
    public object? Encode<T>(ServerEvent<T> serverEvent)
    {
        var wire = Map(serverEvent.Type, serverEvent.Data);

        if (wire is null)
        {
            // Said once per type: a plugin publishing something the stream cannot carry would otherwise
            // fill the log with every publish.
            if (_unencodable.TryAdd(serverEvent.Type, true))
                Console.Error.WriteLine(
                    $"[raindrop] Event '{serverEvent.Type}' ({typeof(T).Name}) cannot travel on the event stream: " +
                    "a plugin's event payload must be a protobuf message. Clients will not receive it.");

            return null;
        }

        return new Proto.ConnectResponse { Event = wire };
    }

    private static Proto.ServerEvent? Map(string type, object? data) => (type, data) switch
    {
        (ServerEventTypes.Ready, ReadyEvent ready) => new() { Ready = ready.ToProto() },

        (ServerEventTypes.MessageCreated, ChannelMessage message) => new() { MessageCreated = message.ToProto() },
        (ServerEventTypes.MessageDeleted, ChannelMessage message) => new() { MessageDeleted = message.ToProto() },
        (MessageReportService.MessageReported, MessageReportSummary report) => new() { MessageReported = report.ToProto() },

        (ServerEventTypes.ChannelCreated, ChannelSummary channel) => new() { ChannelCreated = channel.ToProto() },
        (ServerEventTypes.ChannelRenamed, ChannelSummary channel) => new() { ChannelRenamed = channel.ToProto() },
        (ServerEventTypes.ChannelDeleted, ChannelDeletedEvent deleted) =>
            new() { ChannelDeleted = new Proto.ChannelDeletedEvent { ChannelId = Text(deleted.ChannelId) } },

        (ServerEventTypes.MemberJoined, MemberEvent member) => new() { MemberJoined = member.ToProto() },
        (ServerEventTypes.MemberRenamed, MemberEvent member) => new() { MemberRenamed = member.ToProto() },
        (ServerEventTypes.MemberLeft, MemberEvent member) => new() { MemberLeft = member.ToProto() },
        (ServerEventTypes.MemberPresence, MemberPresenceEvent presence) => new()
        {
            MemberPresence = new Proto.MemberPresenceEvent { UserId = Text(presence.UserId), Presence = Presence(presence.Presence) }
        },
        (ServerEventTypes.MemberRoles, MemberRolesEvent roles) => new()
        {
            MemberRoles = new Proto.MemberRolesEvent
            {
                UserId = Text(roles.UserId), RoleId = Text(roles.RoleId), Assigned = roles.Assigned
            }
        },

        (ServerEventTypes.RoleCreated, RoleSummary role) => new() { RoleCreated = role.ToProto() },
        (ServerEventTypes.RoleUpdated, RoleSummary role) => new() { RoleUpdated = role.ToProto() },
        (ServerEventTypes.RoleDeleted, RoleDeletedEvent deleted) =>
            new() { RoleDeleted = new Proto.RoleDeletedEvent { RoleId = Text(deleted.RoleId) } },

        (ServerEventTypes.VoiceState, VoiceParticipant participant) => new() { VoiceState = participant.ToProto() },
        (ServerEventTypes.VoiceLeft, VoiceLeftEvent left) => new()
        {
            VoiceLeft = new Proto.VoiceLeftEvent
            {
                ChannelId = Text(left.ChannelId), UserId = Text(left.UserId), Reason = left.Reason
            }
        },

        (E2eeEventTypes.DeviceAdded, DeviceEvent device) => new() { DeviceAdded = Device(device) },
        (E2eeEventTypes.DeviceRevoked, DeviceEvent device) => new() { DeviceRevoked = Device(device) },
        (E2eeEventTypes.IdentityReset, IdentityResetEvent reset) =>
            new() { IdentityReset = new Proto.IdentityResetEvent { UserId = Text(reset.UserId) } },
        (DeliveryEventTypes.Handshake, HandshakeEvent handshake) => new()
        {
            Handshake = new Proto.HandshakeEvent
            {
                ChannelId = Text(handshake.ChannelId), Epoch = handshake.Epoch, Message = Bytes(handshake.Message)
            }
        },
        (DeliveryEventTypes.Welcome, WelcomeEvent welcome) => new()
        {
            Welcome = new Proto.WelcomeEvent { ChannelId = Text(welcome.ChannelId), DeviceId = Text(welcome.DeviceId) }
        },
        (DeliveryEventTypes.GroupReconcile, GroupReconcileEvent reconcile) =>
            new() { GroupReconcile = new Proto.GroupReconcileEvent { ChannelId = Text(reconcile.ChannelId) } },
        (DeliveryEventTypes.KeyPackagesLow, KeyPackagesLowEvent low) => new()
        {
            KeyPackagesLow = new Proto.KeyPackagesLowEvent { DeviceId = Text(low.DeviceId), Remaining = low.Remaining }
        },

        // Anything else is a plugin's. A built-in type arriving with an unexpected payload falls through
        // to here too, and is refused below unless that payload happens to be a protobuf message.
        (_, IMessage message) => new() { Plugin = new Proto.PluginEvent { Type = type, Data = message.ToByteString() } },
        _ => null
    };

    private static Proto.DeviceEvent Device(DeviceEvent device) => new()
    {
        UserId = Text(device.UserId), DeviceId = Text(device.DeviceId), Certificate = Bytes(device.Certificate)
    };
}
