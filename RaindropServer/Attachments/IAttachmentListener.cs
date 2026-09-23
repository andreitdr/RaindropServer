namespace RaindropServer.Attachments;

/// <summary>
/// Told when an attachment reaches a new stage — a plugin keeping its own index, mirroring files, or
/// counting storage per member. Register with <c>TryAddEnumerable</c> so every listener runs.
/// </summary>
/// <remarks>
/// Listeners are told after the change is written, and one that throws is reported and stepped over,
/// as <see cref="Users.IRegistrationListener"/>s are: the upload has already happened, and failing the
/// member's request over a plugin's bookkeeping would help nobody. Every method has a do-nothing
/// default, so a listener implements only what it cares about.
/// </remarks>
public interface IAttachmentListener
{
    /// <summary>Every byte of an upload has arrived and matched its hash.</summary>
    Task OnUploadedAsync(AttachmentRecord attachment, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Attachments were sent in a message.</summary>
    Task OnAttachedAsync(Guid messageId, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>An attachment's file has been removed from storage for good.</summary>
    Task OnRemovedAsync(AttachmentRecord attachment, CancellationToken cancellationToken) => Task.CompletedTask;
}
