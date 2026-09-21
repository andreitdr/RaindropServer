namespace RaindropServer.Channels.Text;

/// <summary>
/// Something that needs to know that message text is about to be destroyed. Registered in the
/// container and called by <see cref="TextChannelService"/> and <see cref="TextChannelModule"/>
/// <b>before</b> the text goes, while it can still be read.
/// </summary>
/// <remarks>
/// This is the seam that keeps deleted messages preserved: <see cref="ArchivingMessageDeletionListener"/>
/// is the default listener and copies them into the database; a plugin can register another to
/// forward them somewhere else, alert a moderator, and so on. Register with
/// <c>TryAddEnumerable</c> so every listener runs.
/// <para>
/// Unlike <see cref="Users.IRegistrationListener"/>, a listener that throws <b>stops the deletion</b>:
/// nothing is written and nothing is published. That is deliberate — a preservation guarantee that
/// fails silently is worse than a delete that visibly fails and can be retried. Listeners therefore
/// have to be idempotent, since the deletion is retried from the start after a failure.
/// </para>
/// <para>
/// Both methods have a do-nothing default, so a listener implements only what it cares about.
/// </para>
/// </remarks>
public interface IMessageDeletionListener
{
    /// <summary>
    /// Called once for a single message that is about to become a tombstone. Never called for a
    /// message that already is one.
    /// </summary>
    Task OnMessageDeletingAsync(MessageDeletion deletion, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Called once when a whole text channel is about to be deleted, before any of its messages go.
    /// The messages themselves are not passed along — a channel can hold a great many — so a listener
    /// that wants them reads them from <see cref="ITextMessageStore"/>.
    /// </summary>
    Task OnChannelMessagesDeletingAsync(ChannelDeletion deletion, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>A message that is about to be deleted, and who is deleting it.</summary>
/// <param name="Message">The message as it is now, its <see cref="TextMessageRecord.Content"/> still intact.</param>
/// <param name="Channel">The channel it was posted in.</param>
/// <param name="DeletedBy">The UUID of the member deleting it: the author, or a moderator.</param>
/// <param name="DeletedAtUtc">When the deletion was requested.</param>
public sealed record MessageDeletion(
    TextMessageRecord Message, ChannelRecord Channel, Guid DeletedBy, DateTime DeletedAtUtc);
