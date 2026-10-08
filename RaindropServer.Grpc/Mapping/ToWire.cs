using RaindropServer.Attachments;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.E2ee;
using RaindropServer.Events;
using RaindropServer.Members;
using RaindropServer.Roles;
using RaindropServer.Users;
using Google.Protobuf.WellKnownTypes;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Mapping;

/// <summary>
/// The library's contracts as protobuf messages. One direction only: what the domain returns, on its way
/// out. Requests are read field by field in the services, where each one's checks belong.
/// </summary>
/// <remarks>
/// proto3 refuses null for a field, so every optional value is set only when there is one — absent on
/// the wire is how null travels.
/// </remarks>
internal static class ToWire
{
    // ── Users ────────────────────────────────────────────────────────────────

    public static Proto.UserProfile ToProto(this UserProfile profile)
    {
        var message = new Proto.UserProfile
        {
            UserId = Text(profile.UserId),
            Username = profile.Username,
            State = UserState(profile.State),
            CreatedAt = Time(profile.CreatedAtUtc)
        };

        if (profile.LastLoginAtUtc is { } lastLogin)
            message.LastLoginAt = Time(lastLogin);

        return message;
    }

    public static Proto.PublicUserProfile ToProto(this PublicUserProfile profile)
    {
        var message = new Proto.PublicUserProfile { UserId = Text(profile.UserId), State = UserState(profile.State) };

        if (profile.Username is { } username)
            message.Username = username;

        return message;
    }

    public static Proto.Session ToProto(this SessionResponse session) => new()
    {
        User = session.User.ToProto(),
        Token = session.Token,
        ExpiresAt = Time(session.ExpiresAtUtc)
    };

    // ── Members ──────────────────────────────────────────────────────────────

    public static Proto.Member ToProto(this Member member) => new()
    {
        UserId = Text(member.UserId),
        Username = member.Username,
        Presence = Presence(member.Presence)
    };

    // ── Roles ────────────────────────────────────────────────────────────────

    public static Proto.Role ToProto(this RoleSummary role) => new()
    {
        RoleId = Text(role.RoleId),
        Name = role.Name,
        Kind = RoleKind(role.Kind),
        Position = role.Position,
        Permissions = { role.Permissions },
        CreatedAt = Time(role.CreatedAtUtc)
    };

    // ── Channels ─────────────────────────────────────────────────────────────

    public static Proto.Channel ToProto(this ChannelSummary channel) => new()
    {
        ChannelId = Text(channel.ChannelId),
        Type = channel.Type,
        Name = channel.Name,
        CreatedBy = Text(channel.CreatedBy),
        CreatedAt = Time(channel.CreatedAtUtc),
        YourPermissions = { channel.YourPermissions }
    };

    // ── Text ─────────────────────────────────────────────────────────────────

    public static Proto.ChannelMessage ToProto(this ChannelMessage message)
    {
        var wire = new Proto.ChannelMessage
        {
            MessageId = Text(message.MessageId),
            ChannelId = Text(message.ChannelId),
            AuthorId = Text(message.AuthorId),
            SentAt = Time(message.SentAtUtc),
            Deleted = message.Deleted
        };

        if (message.Content is { } content)
            wire.Content = content;
        if (message.DeletedAtUtc is { } deletedAt)
            wire.DeletedAt = Time(deletedAt);
        if (message.DeletedBy is { } deletedBy)
            wire.DeletedBy = Text(deletedBy);
        if (message.Ciphertext is { } ciphertext)
            wire.Ciphertext = Bytes(ciphertext);
        if (message.Epoch is { } epoch)
            wire.Epoch = epoch;
        if (message.SenderDeviceId is { } device)
            wire.SenderDeviceId = Text(device);
        if (message.Attachments is { } attachments)
            wire.Attachments.Add(attachments.Select(ToProto));

        return wire;
    }

    public static Proto.MessageAttachment ToProto(this MessageAttachment attachment)
    {
        var wire = new Proto.MessageAttachment
        {
            AttachmentId = Text(attachment.AttachmentId),
            Kind = AttachmentKind(attachment.Kind),
            Extension = attachment.Extension,
            CiphertextSize = attachment.CiphertextSize
        };

        if (attachment.CiphertextSha256 is { } hash)
            wire.CiphertextSha256 = Bytes(hash);

        return wire;
    }

    public static Proto.MessageReport ToProto(this MessageReportSummary report)
    {
        var wire = new Proto.MessageReport
        {
            ReportId = Text(report.ReportId),
            MessageId = Text(report.MessageId),
            ChannelId = Text(report.ChannelId),
            AuthorId = Text(report.AuthorId),
            SenderDeviceId = Text(report.SenderDeviceId),
            ReporterId = Text(report.ReporterId),
            Body = report.Body,
            MessageSentAt = Time(report.MessageSentAtUtc),
            ReportedAt = Time(report.ReportedAtUtc)
        };

        if (report.Reason is { } reason)
            wire.Reason = reason;

        return wire;
    }

    // ── Voice ────────────────────────────────────────────────────────────────

    public static Proto.VoiceParticipant ToProto(this VoiceParticipant participant) => new()
    {
        ChannelId = Text(participant.ChannelId),
        UserId = Text(participant.UserId),
        SelfMute = participant.SelfMute,
        SelfDeaf = participant.SelfDeaf,
        ServerMute = participant.ServerMute,
        Suppressed = participant.Suppressed,
        Connected = participant.Connected,
        JoinedAt = Time(participant.JoinedAtUtc)
    };

    // ── E2EE ─────────────────────────────────────────────────────────────────

    public static Proto.Device ToProto(this DeviceSummary device) => new()
    {
        DeviceId = Text(device.DeviceId),
        Certificate = Bytes(device.Certificate),
        CreatedAt = Time(device.CreatedAtUtc)
    };

    public static Proto.UserDevices ToProto(this UserDevices user)
    {
        var wire = new Proto.UserDevices { UserId = Text(user.UserId), Devices = { user.Devices.Select(ToProto) } };

        if (user.IdentityKey is { } key)
            wire.IdentityKey = Bytes(key);

        return wire;
    }

    public static Proto.GroupState ToProto(this GroupStateResponse state)
    {
        var wire = new Proto.GroupState
        {
            ChannelId = Text(state.ChannelId),
            Epoch = state.Epoch,
            GroupInfoEpoch = state.GroupInfoEpoch
        };

        if (state.GroupInfo is { } info)
            wire.GroupInfo = Bytes(info);

        return wire;
    }

    public static Proto.DeviceRef ToProto(this DeviceRef device) => new()
    {
        UserId = Text(device.UserId),
        DeviceId = Text(device.DeviceId)
    };

    public static Proto.PendingWelcome ToProto(this PendingWelcome welcome) => new()
    {
        WelcomeId = Text(welcome.WelcomeId),
        ChannelId = Text(welcome.ChannelId),
        Epoch = welcome.Epoch,
        Welcome = Bytes(welcome.Welcome)
    };

    // ── Attachments ──────────────────────────────────────────────────────────

    public static Proto.AttachmentPolicy ToProto(this AttachmentPolicyResponse policy, int frameBytes)
    {
        var wire = new Proto.AttachmentPolicy
        {
            Enabled = policy.Enabled,
            MaximumFileBytes = policy.MaximumFileBytes,
            MaximumFileBytesByExtension = { policy.MaximumFileBytesByExtension.ToDictionary() },
            BlockedExtensions = { policy.BlockedExtensions },
            AllowFilesWithoutExtension = policy.AllowFilesWithoutExtension,
            MaximumFilesPerMessage = policy.MaximumFilesPerMessage,
            MaximumThumbnailBytes = policy.MaximumThumbnailBytes,
            PreviewExtensions = { policy.PreviewExtensions },
            PreviewAutoLoadMaximumBytes = policy.PreviewAutoLoadMaximumBytes,
            UploadFrameBytes = frameBytes
        };

        if (policy.AllowedExtensions is { } allowed)
            wire.AllowedExtensions = new Proto.ExtensionList { Extensions = { allowed } };

        return wire;
    }

    public static Proto.UploadStatus ToProto(this UploadStatus status, int frameBytes) => new()
    {
        AttachmentId = Text(status.AttachmentId),
        State = UploadState(status.State),
        CiphertextSize = status.CiphertextSize,
        ReceivedSize = status.ReceivedSize,
        FrameBytes = frameBytes,
        ExpiresAt = Time(status.ExpiresAtUtc)
    };

    // ── Events ───────────────────────────────────────────────────────────────

    public static Proto.ReadyEvent ToProto(this ReadyEvent ready) => new()
    {
        UserId = Text(ready.UserId),
        ProtocolVersion = ready.ProtocolVersion,
        HeartbeatInterval = Duration.FromTimeSpan(ready.HeartbeatInterval)
    };

    public static Proto.MemberEvent ToProto(this MemberEvent member)
    {
        var wire = new Proto.MemberEvent { UserId = Text(member.UserId) };

        if (member.Username is { } username)
            wire.Username = username;

        return wire;
    }
}
