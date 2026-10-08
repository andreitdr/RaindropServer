using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using RaindropServer.Grpc.Mapping;
using RaindropServer.Members;
using RaindropServer.Users;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Services;

/// <summary>The handshake: what this server is and how to trust it.</summary>
public sealed class ServerGrpcService : Proto.ServerService.ServerServiceBase
{
    private readonly ServerIdentity _identity;

    public ServerGrpcService(ServerIdentity identity) => _identity = identity;

    [AllowAnonymous]
    public override Task<Proto.GetServerInfoResponse> GetServerInfo(
        Proto.GetServerInfoRequest request, ServerCallContext context)
    {
        var response = new Proto.GetServerInfoResponse
        {
            ProtocolVersion = _identity.ProtocolVersion,
            Capabilities = { _identity.Capabilities }
        };

        if (_identity.TlsFingerprint is { } fingerprint)
            response.TlsFingerprint = fingerprint;
        if (_identity.IdentityKey is { } key)
            response.IdentityKey = Bytes(key);
        if (_identity.TlsBindingSignature is { } signature)
            response.TlsBindingSignature = Bytes(signature);

        return Task.FromResult(response);
    }
}

/// <summary>Accounts and sessions; every rule is <see cref="UserService"/>'s.</summary>
public sealed class UserGrpcService : Proto.UserService.UserServiceBase
{
    private readonly UserService _users;

    public UserGrpcService(UserService users) => _users = users;

    [AllowAnonymous]
    public override async Task<Proto.GetUserStatusResponse> GetUserStatus(
        Proto.GetUserStatusRequest request, ServerCallContext context)
    {
        var state = await _users.GetStateAsync(Id(request.UserId, "user_id"), context.CancellationToken);

        var response = new Proto.GetUserStatusResponse { UserId = Text(state.UserId), State = UserState(state.State) };
        if (state.Username is { } username)
            response.Username = username;
        if (state.DataExpiresAtUtc is { } expires)
            response.DataExpiresAt = Time(expires);

        return response;
    }

    [AllowAnonymous]
    public override async Task<Proto.CheckUsernameResponse> CheckUsername(
        Proto.CheckUsernameRequest request, ServerCallContext context)
    {
        var result = await _users.CheckUsernameAsync(request.Username, context.CancellationToken);

        var response = new Proto.CheckUsernameResponse { Username = result.Username, Available = result.Available };
        if (result.Reason is { } reason)
            response.Reason = reason;

        return response;
    }

    [AllowAnonymous]
    public override async Task<Proto.RegisterResponse> Register(Proto.RegisterRequest request, ServerCallContext context)
    {
        var session = await _users.RegisterAsync(
            new RegisterRequest(Id(request.UserId, "user_id"), request.Username, request.Password),
            context.CancellationToken);

        return new Proto.RegisterResponse { Session = session.ToProto() };
    }

    [AllowAnonymous]
    public override async Task<Proto.LoginResponse> Login(Proto.LoginRequest request, ServerCallContext context)
    {
        var session = await _users.LoginAsync(
            new LoginRequest(
                OptionalId(request.HasUserId, request.UserId, "user_id"),
                request.HasUsername ? request.Username : null,
                request.Password),
            context.CancellationToken);

        return new Proto.LoginResponse { Session = session.ToProto() };
    }

    [AllowAnonymous]
    public override async Task<Proto.RejoinResponse> Rejoin(Proto.RejoinRequest request, ServerCallContext context)
    {
        var session = await _users.RejoinAsync(
            new RejoinRequest(
                OptionalId(request.HasUserId, request.UserId, "user_id"),
                request.HasUsername ? request.Username : null,
                request.Password),
            context.CancellationToken);

        return new Proto.RejoinResponse { Session = session.ToProto() };
    }

    public override async Task<Proto.LogoutResponse> Logout(Proto.LogoutRequest request, ServerCallContext context)
    {
        var result = await _users.LogoutAsync(context.Session(), context.CancellationToken);
        return new Proto.LogoutResponse { Message = result.Message };
    }

    public override async Task<Proto.GetMeResponse> GetMe(Proto.GetMeRequest request, ServerCallContext context) =>
        new() { User = (await _users.GetProfileAsync(context.Session(), context.CancellationToken)).ToProto() };

    public override async Task<Proto.LookupUserResponse> LookupUser(
        Proto.LookupUserRequest request, ServerCallContext context) =>
        new() { User = (await _users.LookupAsync(Id(request.UserId, "user_id"), context.CancellationToken)).ToProto() };

    public override async Task<Proto.ChangePasswordResponse> ChangePassword(
        Proto.ChangePasswordRequest request, ServerCallContext context)
    {
        var session = await _users.ChangePasswordAsync(context.Session(),
            new ChangePasswordRequest(request.CurrentPassword, request.NewPassword), context.CancellationToken);

        return new Proto.ChangePasswordResponse { Session = session.ToProto() };
    }

    public override async Task<Proto.ChangeUsernameResponse> ChangeUsername(
        Proto.ChangeUsernameRequest request, ServerCallContext context)
    {
        var profile = await _users.ChangeUsernameAsync(context.Session(),
            new ChangeUsernameRequest(request.Username), context.CancellationToken);

        return new Proto.ChangeUsernameResponse { User = profile.ToProto() };
    }

    public override async Task<Proto.LeaveServerResponse> LeaveServer(
        Proto.LeaveServerRequest request, ServerCallContext context)
    {
        var result = await _users.LeaveAsync(context.Session(),
            new LeaveRequest(request.Password, request.PreserveData), context.CancellationToken);

        var response = new Proto.LeaveServerResponse { State = UserState(result.State), Message = result.Message };
        if (result.DataExpiresAtUtc is { } expires)
            response.DataExpiresAt = Time(expires);

        return response;
    }
}

/// <summary>The roster.</summary>
public sealed class MemberGrpcService : Proto.MemberService.MemberServiceBase
{
    private readonly MemberService _members;

    public MemberGrpcService(MemberService members) => _members = members;

    public override async Task<Proto.ListMembersResponse> ListMembers(
        Proto.ListMembersRequest request, ServerCallContext context)
    {
        var roster = await _members.ListAsync(context.Session(), context.CancellationToken);
        return new Proto.ListMembersResponse { Members = { roster.Members.Select(member => member.ToProto()) } };
    }
}
