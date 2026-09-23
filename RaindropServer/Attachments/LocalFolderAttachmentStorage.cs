using System.Runtime.CompilerServices;

namespace RaindropServer.Attachments;

/// <summary>
/// The default <see cref="IAttachmentStorage"/>: encrypted files in a folder on this machine, spread
/// over two levels of subfolders named after the start of each key (<c>ab/cd/abcd…</c>) so no single
/// folder grows to hold every file. An upload in progress is <c>&lt;key&gt;.part</c> beside where it
/// will end up, and becomes the file by a rename.
/// </summary>
public sealed class LocalFolderAttachmentStorage : IAttachmentStorage
{
    private const string PartialSuffix = ".part";

    /// <summary>The folder files are kept in, as an absolute path.</summary>
    public string Root { get; }

    public LocalFolderAttachmentStorage(string root) => Root = Path.GetFullPath(root);

    public LocalFolderAttachmentStorage(AttachmentOptions options) : this(options.StoragePath)
    {
    }

    public Task<Stream> OpenAppendAsync(string key, long offset, CancellationToken cancellationToken)
    {
        var path = PathFor(key) + PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.Asynchronous);

        try
        {
            if (offset > stream.Length)
                throw new InvalidOperationException(
                    $"Upload {key} has {stream.Length} bytes stored, fewer than the {offset} it is being continued from.");

            // Whatever lies past the offset came from a request that failed part-way, and is about to
            // be sent again.
            stream.SetLength(offset);
            stream.Position = offset;
            return Task.FromResult<Stream>(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Task<Stream> OpenPendingReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(OpenForReading(PathFor(key) + PartialSuffix));

    public Task CommitAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        File.Move(path + PartialSuffix, path, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(OpenForReading(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        File.Delete(path);
        File.Delete(path + PartialSuffix);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Key, DateTime LastWrittenUtc)> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);
            var key = name.EndsWith(PartialSuffix, StringComparison.Ordinal) ? name[..^PartialSuffix.Length] : name;

            if (IsKey(key))
                yield return (key, File.GetLastWriteTimeUtc(file));
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Where a key's file lives. Keys are what <see cref="AttachmentRecord.StorageKey"/> makes — 32 hex
    /// digits — and anything else is refused, so a key can never walk out of the folder.
    /// </summary>
    private string PathFor(string key)
    {
        if (!IsKey(key))
            throw new ArgumentException($"'{key}' is not an attachment storage key.", nameof(key));

        return Path.Combine(Root, key[..2], key[2..4], key);
    }

    private static bool IsKey(string key) => key.Length == 32 && key.All(char.IsAsciiHexDigitLower);

    private static FileStream OpenForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous);
}
