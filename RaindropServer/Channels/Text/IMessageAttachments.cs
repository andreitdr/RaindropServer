using RaindropServer.Users;

namespace RaindropServer.Channels.Text;

/// <summary>
/// How text channels hand files to whatever stores them, without knowing what that is: a message names
/// the attachments it carries, and this ties them to it, reads them back for history, and lets go of
/// them if the message never makes it into the database.
/// </summary>
/// <remarks>
/// The default, <see cref="NoMessageAttachments"/>, refuses any message that names one;
/// <c>AddRaindropAttachments</c> registers the real one first. A plugin replaces it by registering its
/// own before either runs.
/// </remarks>
public interface IMessageAttachments
{
    /// <summary>
    /// Ties <paramref name="attachmentIds"/> to a message about to be stored in <paramref name="access"/>'s
    /// channel, refusing with an <see cref="ApiException"/> anything the caller may not attach. All or
    /// nothing: on a refusal none is attached.
    /// </summary>
    Task<IReadOnlyList<MessageAttachment>> AttachAsync(
        UserSession session, ChannelAccess access, Guid messageId, IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken);

    /// <summary>Undoes <see cref="AttachAsync"/> for a message that could not be stored.</summary>
    Task DetachAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Called once the message holding them has been stored.</summary>
    Task OnMessageStoredAsync(Guid messageId, IReadOnlyList<MessageAttachment> attachments, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>The attachments of each of these messages that has any.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageAttachment>>> ListAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);
}

/// <summary>The stand-in when this server stores no attachments: a message naming one is refused.</summary>
public sealed class NoMessageAttachments : IMessageAttachments
{
    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<MessageAttachment>> None =
        new Dictionary<Guid, IReadOnlyList<MessageAttachment>>();

    public Task<IReadOnlyList<MessageAttachment>> AttachAsync(
        UserSession session, ChannelAccess access, Guid messageId, IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken) =>
        throw ApiException.BadRequest("attachments_disabled", "This server does not accept files.");

    public Task DetachAsync(Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageAttachment>>> ListAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken) =>
        Task.FromResult(None);
}
