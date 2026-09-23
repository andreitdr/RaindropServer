using RaindropServer.Configuration;

namespace RaindropServer.Attachments;

/// <summary>
/// Makes sure the folder attachments go in can be written to, and says so when the disk under it has
/// less room than the largest file this server accepts — found out at startup rather than from the
/// first member whose upload fails. Checks only the default <see cref="LocalFolderAttachmentStorage"/>;
/// a plugin that stores files elsewhere registers its own check.
/// </summary>
public sealed class AttachmentStorageStartupCheck : IStartupCheck
{
    private readonly IAttachmentStorage _storage;
    private readonly AttachmentOptions _options;

    public AttachmentStorageStartupCheck(IAttachmentStorage storage, AttachmentOptions options)
    {
        _storage = storage;
        _options = options;
    }

    public string Name => "attachments";

    public Task CheckAsync(ConfigurationReport report, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _storage is not LocalFolderAttachmentStorage local)
            return Task.CompletedTask;

        var section = report.Section("attachments");

        try
        {
            Directory.CreateDirectory(local.Root);

            var probe = Path.Combine(local.Root, $".write-check-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            section.Error("storagePath", $"'{local.Root}' cannot be written to: {ex.Message}",
                "Point it at a folder this server can create files in.");

            return Task.CompletedTask;
        }

        try
        {
            var free = new DriveInfo(local.Root).AvailableFreeSpace;
            var largest = AttachmentFormat.CiphertextLength(_options.LargestAcceptedBytes);

            if (free < largest)
                section.Warn("storagePath",
                    $"'{local.Root}' has {AttachmentOptions.Describe(free)} free, less than the " +
                    $"{AttachmentOptions.Describe(largest)} one file may take up.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Free space is a courtesy; a file system that will not say is not a reason to stop.
        }

        return Task.CompletedTask;
    }
}
