using System.Text.RegularExpressions;
using RaindropServer.Configuration;

namespace RaindropServer.Attachments;

/// <summary>
/// Server-owner-tunable rules for files added to messages: how large, of which types, and which types
/// clients show inline.
/// </summary>
/// <remarks>
/// Files are end-to-end encrypted like the messages that carry them, so everything here is judged on
/// what an upload declares in the clear — its extension, and a ciphertext length that fixes the
/// plaintext length exactly (<see cref="AttachmentFormat"/>). The server never sees a file's name or
/// contents. Sizes are held in bytes; <c>raindrop.json</c> writes them in megabytes, where one
/// megabyte is <see cref="Megabyte"/> bytes.
/// </remarks>
public sealed partial class AttachmentOptions
{
    /// <summary>One megabyte as <c>raindrop.json</c> counts it.</summary>
    public const long Megabyte = 1024 * 1024;

    /// <summary>The longest extension accepted, in characters.</summary>
    public const int MaximumExtensionLength = 16;

    /// <summary>
    /// What the shipped configuration blocks: files that run as programs when opened on common desktop
    /// systems. A blocklist is a courtesy rather than a defence — any file can be renamed — which is
    /// why clients never open a received file by themselves either.
    /// </summary>
    public static IReadOnlyList<string> DefaultBlockedExtensions { get; } =
    [
        "exe", "msi", "bat", "cmd", "com", "scr", "pif", "vbs", "vbe", "jse", "wsf", "hta", "cpl", "ps1", "lnk",
        "jar", "apk"
    ];

    /// <summary>The types the shipped configuration lets clients preview inline.</summary>
    public static IReadOnlyList<string> DefaultPreviewExtensions { get; } =
    [
        "png", "jpg", "jpeg", "gif", "webp", "mp4", "webm", "mov", "mp3", "m4a", "ogg", "wav", "flac", "opus"
    ];

    /// <summary>Whether members may add files to messages at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The folder the default storage keeps encrypted files in. Relative paths are resolved by the host
    /// (next to the executable for the CLI).
    /// </summary>
    public string StoragePath { get; set; } = "attachments";

    /// <summary>The largest file accepted, in bytes of plaintext, unless its extension has its own limit.</summary>
    public long MaximumFileBytes { get; set; } = 25 * Megabyte;

    /// <summary>
    /// Limits for particular extensions, larger or smaller than <see cref="MaximumFileBytes"/>. Keys are
    /// lower-case extensions without the dot.
    /// </summary>
    public IDictionary<string, long> MaximumFileBytesByExtension { get; set; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The only extensions accepted, or null to accept any that is not blocked.</summary>
    public IReadOnlyList<string>? AllowedExtensions { get; set; }

    /// <summary>Extensions never accepted, whatever <see cref="AllowedExtensions"/> says.</summary>
    public IReadOnlyList<string> BlockedExtensions { get; set; } = DefaultBlockedExtensions;

    /// <summary>Whether a file whose name has no extension may be uploaded.</summary>
    public bool AllowFilesWithoutExtension { get; set; } = true;

    /// <summary>The most files one message may carry, not counting their thumbnails.</summary>
    public int MaximumFilesPerMessage { get; set; } = 10;

    /// <summary>How long an upload may wait to be attached to a message before it is thrown away.</summary>
    public TimeSpan PendingUploadExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How many uploads one member may have that are not yet part of a message — the bound on how much
    /// storage somebody can take up without ever posting.
    /// </summary>
    public int MaximumPendingUploadsPerMember { get; set; } = 20;

    /// <summary>
    /// Extensions clients show inline (images, video, audio) when they know how to render them;
    /// everything else is shown as a file to save.
    /// </summary>
    public IReadOnlyList<string> PreviewExtensions { get; set; } = DefaultPreviewExtensions;

    /// <summary>
    /// The largest previewable file a client fetches and shows by itself; a larger one waits for the
    /// member to ask for it.
    /// </summary>
    public long PreviewAutoLoadMaximumBytes { get; set; } = 10 * Megabyte;

    /// <summary>
    /// The most bytes one upload request may carry. Not in <c>raindrop.json</c>: it is a protocol
    /// detail, kept small enough to pass the body limits of common reverse proxies.
    /// </summary>
    public int UploadChunkBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>The largest thumbnail accepted, in bytes of plaintext. Thumbnails skip the type rules.</summary>
    public long MaximumThumbnailBytes { get; set; } = 512 * 1024;

    /// <summary>How often expired uploads and the files of deleted messages are cleared away.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(10);

    [GeneratedRegex("^[a-z0-9]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    /// <summary>
    /// Whether <paramref name="extension"/> is written the way this server compares extensions: lower
    /// case letters and digits, no dot. The empty string means "no extension".
    /// </summary>
    public static bool IsWellFormedExtension(string extension) =>
        extension.Length == 0 || ExtensionPattern().IsMatch(extension);

    /// <summary>Whether a file with this extension (empty for none) may be uploaded.</summary>
    public bool IsTypeAllowed(string extension)
    {
        if (extension.Length == 0)
            return AllowFilesWithoutExtension;

        if (BlockedExtensions.Contains(extension, StringComparer.Ordinal))
            return false;

        return AllowedExtensions is null || AllowedExtensions.Contains(extension, StringComparer.Ordinal);
    }

    /// <summary>The largest file accepted with this extension, in bytes of plaintext.</summary>
    public long MaximumBytesFor(string extension) =>
        MaximumFileBytesByExtension.TryGetValue(extension, out var limit) ? limit : MaximumFileBytes;

    /// <summary>The largest file this server accepts of any type — what its storage has to have room for.</summary>
    public long LargestAcceptedBytes =>
        MaximumFileBytesByExtension
            .Where(entry => IsTypeAllowed(entry.Key))
            .Select(entry => entry.Value)
            .Append(MaximumFileBytes)
            .Max();

    /// <summary>
    /// Reports every rule that is malformed, impossible, or contradicts another — named as
    /// <c>raindrop.json</c> names them.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        if (string.IsNullOrWhiteSpace(StoragePath))
            report.Error("storagePath", "is empty; encrypted files need a folder to be kept in.");

        ValidateSize(report, "maximumFileSizeMegabytes", MaximumFileBytes);

        var blocked = ValidateExtensions(report, "blockedExtensions", BlockedExtensions);
        var allowed = AllowedExtensions is null ? null : ValidateExtensions(report, "allowedExtensions", AllowedExtensions);
        var preview = ValidateExtensions(report, "preview.extensions", PreviewExtensions);

        if (allowed is not null)
        {
            foreach (var both in allowed.Intersect(blocked, StringComparer.Ordinal))
                report.Error("allowedExtensions",
                    $"lists '{both}', which blockedExtensions also lists, so it can never be uploaded.",
                    "A blocked extension always wins; remove it from one of the two.");

            if (allowed.Count == 0 && !AllowFilesWithoutExtension)
                report.Error("allowedExtensions",
                    "is empty and allowFilesWithoutExtension is false, so no file can be uploaded at all.",
                    "Set enabled to false to switch attachments off instead.");
        }

        foreach (var (extension, limit) in MaximumFileBytesByExtension)
        {
            var setting = $"maximumFileSizeMegabytesByExtension.{extension}";

            if (!CheckExtension(report, "maximumFileSizeMegabytesByExtension", extension))
                continue;

            ValidateSize(report, setting, limit);

            if (!IsTypeAllowed(extension))
                report.Error(setting,
                    $"sets a limit for '{extension}' files, which this server never accepts.",
                    "Remove the limit, or allow the extension.");
        }

        foreach (var extension in preview.Where(extension => !IsTypeAllowed(extension)))
            report.Warn("preview.extensions",
                $"lists '{extension}', which this server never accepts, so there is nothing to preview.");

        report.Range("maximumFilesPerMessage", MaximumFilesPerMessage, 1, 100, "file(s)");
        report.Range("maximumPendingUploadsPerMember", MaximumPendingUploadsPerMember, 1, 1000, "upload(s)");
        report.Range("pendingUploadExpiryHours", PendingUploadExpiry, TimeSpan.FromHours(1), TimeSpan.FromDays(30));

        if (MaximumPendingUploadsPerMember < MaximumFilesPerMessage)
            report.Error("maximumPendingUploadsPerMember",
                $"is {MaximumPendingUploadsPerMember}, fewer than the {MaximumFilesPerMessage} files one message may " +
                "carry, so a member could never upload a full message's worth before sending it.");

        if (PreviewAutoLoadMaximumBytes < 0)
            report.Error("preview.autoLoadMaximumMegabytes", "cannot be negative; use 0 to never load previews by themselves.");
        else if (PreviewAutoLoadMaximumBytes > 0 && preview.Count > 0)
        {
            var largestPreview = preview.Where(IsTypeAllowed).Select(MaximumBytesFor).DefaultIfEmpty(0).Max();

            if (PreviewAutoLoadMaximumBytes > largestPreview && largestPreview > 0)
                report.Warn("preview.autoLoadMaximumMegabytes",
                    $"is {Describe(PreviewAutoLoadMaximumBytes)}, more than the {Describe(largestPreview)} the largest " +
                    "previewable file may be, so every preview loads by itself.");
        }

        if (!Enabled)
            report.Warn("enabled", "is false: members cannot add files to messages, and the other attachment settings do nothing.");
    }

    private static void ValidateSize(ConfigurationReport report, string setting, long bytes)
    {
        const long ceiling = 1024L * 1024 * Megabyte; // a terabyte

        if (bytes < Megabyte)
            report.Error(setting, $"is {Describe(bytes)}; a file limit must be at least 1 MB.");
        else if (bytes > ceiling)
            report.Error(setting, $"is {Describe(bytes)}; a single file may be at most 1,048,576 MB (1 TB).");
        else if (bytes > 4096 * Megabyte)
            report.Warn(setting,
                $"is {Describe(bytes)}. Every member who opens a file that large has to download and decrypt all of it.");
    }

    /// <summary>
    /// Checks a list of extensions, reporting each malformed or repeated entry once, and returns the
    /// well-formed ones.
    /// </summary>
    private static List<string> ValidateExtensions(ConfigurationReport report, string setting, IEnumerable<string> extensions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<string>();

        foreach (var extension in extensions)
        {
            if (extension is null || !CheckExtension(report, setting, extension))
                continue;

            if (!seen.Add(extension))
            {
                report.Warn(setting, $"lists '{extension}' more than once.");
                continue;
            }

            valid.Add(extension);
        }

        return valid;
    }

    private static bool CheckExtension(ConfigurationReport report, string setting, string extension)
    {
        if (extension.Length > 0 && ExtensionPattern().IsMatch(extension))
            return true;

        var fixedUp = extension.Trim().TrimStart('.').ToLowerInvariant();
        var remedy = fixedUp.Length > 0 && ExtensionPattern().IsMatch(fixedUp)
            ? $"Write it as \"{fixedUp}\": lower case, without the dot."
            : $"An extension is 1 to {MaximumExtensionLength} lower-case letters and digits, without the dot.";

        report.Error(setting, $"lists \"{extension}\", which is not an extension this server can match.", remedy);
        return false;
    }

    /// <summary>A size the way the configuration file writes it.</summary>
    public static string Describe(long bytes) =>
        bytes % Megabyte == 0 ? $"{bytes / Megabyte:N0} MB" : $"{bytes / (double)Megabyte:N2} MB";
}
