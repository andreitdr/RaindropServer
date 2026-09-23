namespace RaindropServer.Attachments;

/// <summary>
/// Where the encrypted bytes of attachments live. The default,
/// <see cref="LocalFolderAttachmentStorage"/>, keeps them in a folder; a plugin can register another
/// (object storage, a network share) before <c>AddRaindropAttachments</c> runs.
/// </summary>
/// <remarks>
/// A file is written in pieces as its upload arrives, and only becomes readable once
/// <see cref="CommitAsync"/> is called, so a reader never sees half a file. Everything stored here is
/// ciphertext: an implementation never needs to be trusted with anybody's files.
/// </remarks>
public interface IAttachmentStorage
{
    /// <summary>
    /// Opens the upload in progress for <paramref name="key"/> for appending, positioned at
    /// <paramref name="offset"/>. Anything past the offset — left by an earlier request that failed
    /// part-way — is discarded. An offset beyond what is stored is an error.
    /// </summary>
    Task<Stream> OpenAppendAsync(string key, long offset, CancellationToken cancellationToken);

    /// <summary>Opens the upload in progress for <paramref name="key"/> for reading, to hash it before it is committed.</summary>
    Task<Stream> OpenPendingReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Makes a finished upload the stored file, readable through <see cref="OpenReadAsync"/>.</summary>
    Task CommitAsync(string key, CancellationToken cancellationToken);

    /// <summary>Opens a committed file for reading. The stream is seekable, so ranges can be served.</summary>
    /// <exception cref="FileNotFoundException">There is no committed file under this key.</exception>
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes a file, committed or not. Removing one that is not there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Every key held, with when it was last written — for clearing away files no record points at.
    /// The default lists nothing, which only means such files are never cleared.
    /// </summary>
    IAsyncEnumerable<(string Key, DateTime LastWrittenUtc)> ListAsync(CancellationToken cancellationToken) =>
        AsyncEnumerable.Empty<(string, DateTime)>();
}
