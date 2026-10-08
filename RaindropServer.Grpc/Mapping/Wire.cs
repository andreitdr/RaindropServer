using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RaindropServer.Attachments;
using RaindropServer.Members;
using RaindropServer.Roles;
using RaindropServer.Users;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Mapping;

/// <summary>
/// The scalar conversions every message needs: ids, times, bytes, and the closed sets that are names in
/// the domain and enums on the wire. Everything coming in is checked here, so a malformed id is the
/// same <c>invalid_payload</c> refusal wherever it appears.
/// </summary>
internal static class Wire
{
    // ── Ids ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A required id. Left empty it is <see cref="Guid.Empty"/>, which the domain refuses with the code
    /// that fits (<c>invalid_user_id</c>, <c>channel_not_found</c>, …) exactly as it refused a missing one
    /// before; anything else that is not a UUID is <c>invalid_payload</c>, naming the field.
    /// </summary>
    public static Guid Id(string value, string field) =>
        value.Length == 0 ? Guid.Empty
        : Guid.TryParse(value, out var id) ? id
        : throw ApiException.BadRequest("invalid_payload", $"{field} must be a UUID.");

    /// <summary>An optional id: absent stays null, present must be a UUID.</summary>
    public static Guid? OptionalId(bool present, string value, string field) =>
        present ? Id(value, field) : null;

    /// <summary>Several ids, each checked.</summary>
    public static List<Guid> Ids(IEnumerable<string> values, string field) =>
        values.Select(value => Id(value, field)).ToList();

    /// <summary>
    /// The canonical form ids travel in: lower case, hyphenated. Device certificates and attestations
    /// sign ids as exactly this text.
    /// </summary>
    public static string Text(Guid id) => id.ToString("D");

    // ── Times ────────────────────────────────────────────────────────────────

    public static Timestamp Time(DateTime value) => Timestamp.FromDateTime(value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),

        // The stores read UTC back without saying so.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    });

    public static Timestamp? Time(DateTime? value) => value is { } time ? Time(time) : null;

    // ── Bytes ────────────────────────────────────────────────────────────────

    public static ByteString Bytes(byte[] value) => ByteString.CopyFrom(value);

    /// <summary>An optional bytes field the domain expects as null when it was not sent.</summary>
    public static byte[]? OptionalBytes(bool present, ByteString value) => present ? value.ToByteArray() : null;

    // ── Closed sets ──────────────────────────────────────────────────────────

    public static Proto.UserState UserState(string state) => state switch
    {
        StateNames.Unregistered => Proto.UserState.Unregistered,
        StateNames.Active => Proto.UserState.Active,
        StateNames.LeftPreserved => Proto.UserState.LeftPreserved,
        StateNames.Erased => Proto.UserState.Erased,
        _ => Proto.UserState.Unspecified
    };

    public static Proto.Presence Presence(string presence) => presence switch
    {
        PresenceStatusNames.Online => Proto.Presence.Online,
        PresenceStatusNames.Offline => Proto.Presence.Offline,
        _ => Proto.Presence.Unspecified
    };

    public static Proto.RoleKind RoleKind(string kind) => kind switch
    {
        RoleKindNames.Custom => Proto.RoleKind.Custom,
        RoleKindNames.Everyone => Proto.RoleKind.Everyone,
        RoleKindNames.Owner => Proto.RoleKind.Owner,
        _ => Proto.RoleKind.Unspecified
    };

    public static Proto.AttachmentKind AttachmentKind(string kind) => kind switch
    {
        AttachmentNames.FileKind => Proto.AttachmentKind.File,
        AttachmentNames.ThumbnailKind => Proto.AttachmentKind.Thumbnail,
        _ => Proto.AttachmentKind.Unspecified
    };

    /// <summary>Unspecified is the default, a file; a value this build does not know is refused.</summary>
    public static string AttachmentKind(Proto.AttachmentKind kind) => kind switch
    {
        Proto.AttachmentKind.Unspecified or Proto.AttachmentKind.File => AttachmentNames.FileKind,
        Proto.AttachmentKind.Thumbnail => AttachmentNames.ThumbnailKind,
        _ => throw ApiException.BadRequest("invalid_attachment", $"Unknown attachment kind {(int)kind}.")
    };

    public static Proto.UploadState UploadState(string state) => state switch
    {
        "uploading" => Proto.UploadState.Uploading,
        "uploaded" => Proto.UploadState.Uploaded,
        "attached" => Proto.UploadState.Attached,
        _ => Proto.UploadState.Unspecified
    };
}
