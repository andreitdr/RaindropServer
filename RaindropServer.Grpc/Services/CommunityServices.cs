using Grpc.Core;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.Grpc.Mapping;
using RaindropServer.Roles;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Services;

/// <summary>Roles and permissions; every rule is <see cref="RoleService"/>'s.</summary>
public sealed class RoleGrpcService : Proto.RoleService.RoleServiceBase
{
    private readonly RoleService _roles;

    public RoleGrpcService(RoleService roles) => _roles = roles;

    public override async Task<Proto.ListRolesResponse> ListRoles(Proto.ListRolesRequest request, ServerCallContext context)
    {
        var roles = await _roles.ListAsync(context.Session(), context.CancellationToken);
        return new Proto.ListRolesResponse { Roles = { roles.Roles.Select(role => role.ToProto()) } };
    }

    public override async Task<Proto.GetMyRolesResponse> GetMyRoles(Proto.GetMyRolesRequest request, ServerCallContext context)
    {
        var session = context.Session();
        var mine = await _roles.GetMemberRolesAsync(session, session.UserId, context.CancellationToken);

        return new Proto.GetMyRolesResponse
        {
            UserId = Text(mine.UserId),
            Roles = { mine.Roles.Select(role => role.ToProto()) },
            Permissions = { mine.Permissions }
        };
    }

    public override Task<Proto.ListPermissionsResponse> ListPermissions(
        Proto.ListPermissionsRequest request, ServerCallContext context) =>
        Task.FromResult(new Proto.ListPermissionsResponse
        {
            Permissions =
            {
                RoleService.Catalogue().Permissions.Select(permission =>
                    new Proto.PermissionDescription { Name = permission.Name, Summary = permission.Summary })
            }
        });

    public override async Task<Proto.GetRoleResponse> GetRole(Proto.GetRoleRequest request, ServerCallContext context)
    {
        var detail = await _roles.GetAsync(context.Session(), new RoleRequest(Id(request.RoleId, "role_id")),
            context.CancellationToken);

        return new Proto.GetRoleResponse { Role = detail.Role.ToProto(), MemberIds = { detail.Members.Select(Text) } };
    }

    public override async Task<Proto.CreateRoleResponse> CreateRole(Proto.CreateRoleRequest request, ServerCallContext context)
    {
        var role = await _roles.CreateAsync(context.Session(),
            new CreateRoleRequest(request.Name, [.. request.Permissions], request.HasPosition ? request.Position : null),
            context.CancellationToken);

        return new Proto.CreateRoleResponse { Role = role.ToProto() };
    }

    public override async Task<Proto.UpdateRoleResponse> UpdateRole(Proto.UpdateRoleRequest request, ServerCallContext context)
    {
        var role = await _roles.UpdateAsync(context.Session(),
            new UpdateRoleRequest(
                Id(request.RoleId, "role_id"),
                request.HasName ? request.Name : null,
                request.Permissions is { } set ? [.. set.Permissions] : null,
                request.HasPosition ? request.Position : null),
            context.CancellationToken);

        return new Proto.UpdateRoleResponse { Role = role.ToProto() };
    }

    public override async Task<Proto.DeleteRoleResponse> DeleteRole(Proto.DeleteRoleRequest request, ServerCallContext context)
    {
        var deleted = await _roles.DeleteAsync(context.Session(), new RoleRequest(Id(request.RoleId, "role_id")),
            context.CancellationToken);

        return new Proto.DeleteRoleResponse { RoleId = Text(deleted.RoleId), Message = deleted.Message };
    }

    public override async Task<Proto.GetMemberRolesResponse> GetMemberRoles(
        Proto.GetMemberRolesRequest request, ServerCallContext context)
    {
        var roles = await _roles.GetMemberRolesAsync(context.Session(), Id(request.UserId, "user_id"),
            context.CancellationToken);

        return new Proto.GetMemberRolesResponse
        {
            UserId = Text(roles.UserId),
            Roles = { roles.Roles.Select(role => role.ToProto()) },
            Permissions = { roles.Permissions }
        };
    }

    public override async Task<Proto.AssignRoleResponse> AssignRole(Proto.AssignRoleRequest request, ServerCallContext context)
    {
        var changed = await _roles.AssignAsync(context.Session(),
            new MemberRoleRequest(Id(request.UserId, "user_id"), Id(request.RoleId, "role_id")), context.CancellationToken);

        return new Proto.AssignRoleResponse
        {
            UserId = Text(changed.UserId), RoleId = Text(changed.RoleId), Changed = changed.Changed, Message = changed.Message
        };
    }

    public override async Task<Proto.UnassignRoleResponse> UnassignRole(
        Proto.UnassignRoleRequest request, ServerCallContext context)
    {
        var changed = await _roles.UnassignAsync(context.Session(),
            new MemberRoleRequest(Id(request.UserId, "user_id"), Id(request.RoleId, "role_id")), context.CancellationToken);

        return new Proto.UnassignRoleResponse
        {
            UserId = Text(changed.UserId), RoleId = Text(changed.RoleId), Changed = changed.Changed, Message = changed.Message
        };
    }
}

/// <summary>Channel lifecycle; every rule is <see cref="ChannelService"/>'s.</summary>
public sealed class ChannelGrpcService : Proto.ChannelService.ChannelServiceBase
{
    private readonly ChannelService _channels;

    public ChannelGrpcService(ChannelService channels) => _channels = channels;

    public override async Task<Proto.ListChannelsResponse> ListChannels(
        Proto.ListChannelsRequest request, ServerCallContext context)
    {
        var channels = await _channels.ListAsync(context.Session(), context.CancellationToken);
        return new Proto.ListChannelsResponse { Channels = { channels.Channels.Select(channel => channel.ToProto()) } };
    }

    public override async Task<Proto.GetChannelResponse> GetChannel(Proto.GetChannelRequest request, ServerCallContext context) =>
        new()
        {
            Channel = (await _channels.GetAsync(context.Session(),
                new ChannelRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.CreateChannelResponse> CreateChannel(
        Proto.CreateChannelRequest request, ServerCallContext context) =>
        new()
        {
            Channel = (await _channels.CreateAsync(context.Session(),
                new CreateChannelRequest(request.Type, request.Name), context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.RenameChannelResponse> RenameChannel(
        Proto.RenameChannelRequest request, ServerCallContext context) =>
        new()
        {
            Channel = (await _channels.RenameAsync(context.Session(),
                new RenameChannelRequest(Id(request.ChannelId, "channel_id"), request.Name),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.DeleteChannelResponse> DeleteChannel(
        Proto.DeleteChannelRequest request, ServerCallContext context)
    {
        var deleted = await _channels.DeleteAsync(context.Session(),
            new ChannelRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken);

        return new Proto.DeleteChannelResponse { ChannelId = Text(deleted.ChannelId), Message = deleted.Message };
    }
}

/// <summary>Messages in text channels; every rule is <see cref="TextChannelService"/>'s and <see cref="MessageReportService"/>'s.</summary>
public sealed class TextChannelGrpcService : Proto.TextChannelService.TextChannelServiceBase
{
    private readonly TextChannelService _text;
    private readonly MessageReportService _reports;

    public TextChannelGrpcService(TextChannelService text, MessageReportService reports)
    {
        _text = text;
        _reports = reports;
    }

    public override async Task<Proto.SendMessageResponse> SendMessage(Proto.SendMessageRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _text.SendAsync(context.Session(),
                new SendMessageRequest(Id(request.ChannelId, "channel_id"), request.Content),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.SendEncryptedMessageResponse> SendEncryptedMessage(
        Proto.SendEncryptedMessageRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _text.SendEncryptedAsync(context.Session(),
                new SendEncryptedMessageRequest(
                    Id(request.ChannelId, "channel_id"),
                    request.Message.ToByteArray(),
                    request.AttachmentIds.Count == 0 ? null : Ids(request.AttachmentIds, "attachment_ids")),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.GetHistoryResponse> GetHistory(Proto.GetHistoryRequest request, ServerCallContext context)
    {
        var page = await _text.ReadHistoryAsync(context.Session(),
            new MessageHistoryRequest(
                Id(request.ChannelId, "channel_id"),
                request.HasBefore ? request.Before : null,
                request.HasLimit ? request.Limit : null),
            context.CancellationToken);

        var response = new Proto.GetHistoryResponse
        {
            ChannelId = Text(page.ChannelId),
            Messages = { page.Messages.Select(message => message.ToProto()) }
        };

        if (page.NextCursor is { } cursor)
            response.NextCursor = cursor;

        return response;
    }

    public override async Task<Proto.DeleteMessageResponse> DeleteMessage(
        Proto.DeleteMessageRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _text.DeleteAsync(context.Session(),
                new DeleteMessageRequest(Id(request.MessageId, "message_id")), context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.ReportMessageResponse> ReportMessage(
        Proto.ReportMessageRequest request, ServerCallContext context)
    {
        // Present — even empty — is what makes this a v2 attestation, so absent has to stay null.
        var attested = request.AttestedFiles?.Attachments
            .Select(file => new AttestedAttachment(Id(file.AttachmentId, "attachment_id"), file.CiphertextSha256.ToByteArray()))
            .ToList();

        var report = await _reports.ReportAsync(context.Session(),
            new ReportMessageRequest(
                Id(request.MessageId, "message_id"),
                request.Body,
                request.ClientMessageId,
                request.ClientSentAtUnixMs,
                request.Type,
                request.Attestation.ToByteArray(),
                request.HasReason ? request.Reason : null,
                attested),
            context.CancellationToken);

        return new Proto.ReportMessageResponse { Report = report.ToProto() };
    }

    public override async Task<Proto.ListReportsResponse> ListReports(Proto.ListReportsRequest request, ServerCallContext context)
    {
        var reports = await _reports.ListAsync(context.Session(),
            new ListReportsRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken);

        return new Proto.ListReportsResponse { Reports = { reports.Reports.Select(report => report.ToProto()) } };
    }
}

/// <summary>Voice call control; every rule is <see cref="VoiceService"/>'s. The media itself is WebRTC.</summary>
public sealed class VoiceGrpcService : Proto.VoiceService.VoiceServiceBase
{
    private readonly VoiceService _voice;

    public VoiceGrpcService(VoiceService voice) => _voice = voice;

    public override async Task<Proto.GetVoiceStatusResponse> GetVoiceStatus(
        Proto.GetVoiceStatusRequest request, ServerCallContext context)
    {
        var status = await _voice.StatusAsync(context.Session(), context.CancellationToken);

        return new Proto.GetVoiceStatusResponse
        {
            Available = status.Available,
            Transport = status.Transport,
            MaximumParticipants = status.MaximumParticipants,
            MaximumCameraPublishers = status.MaximumCameraPublishers,
            MaximumScreenShares = status.MaximumScreenShares,
            Participants = { status.Participants.Select(participant => participant.ToProto()) }
        };
    }

    public override async Task<Proto.JoinVoiceResponse> JoinVoice(Proto.JoinVoiceRequest request, ServerCallContext context)
    {
        var joined = await _voice.JoinAsync(context.Session(),
            new JoinVoiceRequest(Id(request.ChannelId, "channel_id"), request.SelfMute, request.SelfDeaf),
            context.CancellationToken);

        var response = new Proto.JoinVoiceResponse
        {
            ChannelId = Text(joined.ChannelId),
            Transport = joined.Transport,
            Token = joined.Token,
            ExpiresAt = Time(joined.ExpiresAtUtc),
            You = joined.You.ToProto(),
            Participants = { joined.Participants.Select(participant => participant.ToProto()) }
        };

        if (joined.Url is { } url)
            response.Url = url;
        if (joined.Path is { } path)
            response.Path = path;

        return response;
    }

    public override async Task<Proto.LeaveVoiceResponse> LeaveVoice(Proto.LeaveVoiceRequest request, ServerCallContext context)
    {
        var left = await _voice.LeaveAsync(context.Session(),
            new LeaveVoiceRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken);

        return new Proto.LeaveVoiceResponse { Left = left.Left };
    }

    public override async Task<Proto.SetVoiceSelfStateResponse> SetVoiceSelfState(
        Proto.SetVoiceSelfStateRequest request, ServerCallContext context) =>
        new()
        {
            Participant = (await _voice.UpdateSelfStateAsync(context.Session(),
                new VoiceSelfStateRequest(
                    request.SelfMute,
                    request.SelfDeaf,
                    request.CameraOn,
                    request.ScreenShareOn,
                    ScreenShareKind(request.ScreenShareKind),
                    request.ScreenShareAudio),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.MuteVoiceMemberResponse> MuteVoiceMember(
        Proto.MuteVoiceMemberRequest request, ServerCallContext context)
    {
        var muted = await _voice.SetServerMuteAsync(context.Session(),
            new VoiceMuteRequest(Id(request.UserId, "user_id"), request.Muted), context.CancellationToken);

        return new Proto.MuteVoiceMemberResponse { UserId = Text(muted.UserId), Muted = muted.Muted, Changed = muted.Changed };
    }
}
