using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using RaindropServer;
using RaindropServer.Attachments;
using RaindropServer.Payloads;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Attachments;

/// <summary>
/// GET /api/attachments/policy — which files this server accepts and which types clients preview
/// inline, so a client can refuse a file before uploading any of it. The server checks again anyway.
/// </summary>
public sealed class AttachmentPolicyEndpoint : IAuthenticatedEndpoint
{
    private readonly AttachmentService _attachments;

    public AttachmentPolicyEndpoint(AttachmentService attachments) => _attachments = attachments;

    public string Route => "/api/attachments/policy";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, _ => Task.FromResult(_attachments.GetPolicy()));
}

/// <summary>
/// POST /api/attachments/uploads/create — starts uploading an encrypted file for a text channel,
/// declaring its extension and ciphertext size. Needs <c>send_messages</c> and <c>attach_files</c> in the
/// channel and a bound session.
/// </summary>
public sealed class CreateUploadEndpoint : IAuthenticatedTypedEndpoint<CreateUploadRequest, UploadStatus>
{
    private readonly AttachmentService _attachments;

    public CreateUploadEndpoint(AttachmentService attachments) => _attachments = attachments;

    public string Route => "/api/attachments/uploads/create";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UploadStatus> HandleAsync(UserSession session, CreateUploadRequest request, CancellationToken cancellationToken) =>
        _attachments.CreateUploadAsync(session, request, cancellationToken);
}

/// <summary>
/// PUT /api/attachments/uploads/chunk — one piece of an upload, as raw bytes
/// (<c>application/octet-stream</c>). Which upload and where the piece starts travel in the
/// <c>Raindrop-Attachment-Id</c> and <c>Raindrop-Upload-Offset</c> headers rather than the URL, which is
/// the line that ends up in logs. Answers with the upload's status, in the negotiated format.
/// </summary>
public sealed class UploadChunkEndpoint : IAuthenticatedEndpoint
{
    public const string AttachmentIdHeader = "Raindrop-Attachment-Id";
    public const string OffsetHeader = "Raindrop-Upload-Offset";

    private readonly AttachmentService _attachments;
    private readonly AttachmentOptions _options;

    public UploadChunkEndpoint(AttachmentService attachments, AttachmentOptions options)
    {
        _attachments = attachments;
        _options = options;
    }

    public string Route => "/api/attachments/uploads/chunk";
    public HttpMethod Method => HttpMethod.Put;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct =>
        {
            if (!Guid.TryParse(context.Request.Headers[AttachmentIdHeader], out var attachmentId))
                throw ApiException.BadRequest("invalid_payload", $"The {AttachmentIdHeader} header must name the upload.");

            if (!long.TryParse(context.Request.Headers[OffsetHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                throw ApiException.BadRequest("invalid_payload", $"The {OffsetHeader} header must say where this chunk starts.");

            // The server-wide body limit is sized for JSON, not files. A chunk may be a little over the
            // chunk size before Kestrel steps in, so the service is the one that says how big is too big.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = _options.UploadChunkBytes + 64 * 1024;

            return _attachments.WriteChunkAsync(session, attachmentId, offset, context.Request.Body, ct);
        });
}

/// <summary>POST /api/attachments/uploads/status — how much of one of your uploads has arrived, to carry on after an interruption.</summary>
public sealed class UploadStatusEndpoint : IAuthenticatedTypedEndpoint<UploadReference, UploadStatus>
{
    private readonly AttachmentService _attachments;

    public UploadStatusEndpoint(AttachmentService attachments) => _attachments = attachments;

    public string Route => "/api/attachments/uploads/status";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UploadStatus> HandleAsync(UserSession session, UploadReference request, CancellationToken cancellationToken) =>
        _attachments.GetStatusAsync(session, request.AttachmentId, cancellationToken);
}

/// <summary>
/// POST /api/attachments/uploads/complete — finishes an upload once every byte has arrived, checking
/// the stored bytes against the SHA-256 the uploader computed. After this it can be sent in a message.
/// </summary>
public sealed class CompleteUploadEndpoint : IAuthenticatedTypedEndpoint<CompleteUploadRequest, UploadStatus>
{
    private readonly AttachmentService _attachments;

    public CompleteUploadEndpoint(AttachmentService attachments) => _attachments = attachments;

    public string Route => "/api/attachments/uploads/complete";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UploadStatus> HandleAsync(UserSession session, CompleteUploadRequest request, CancellationToken cancellationToken) =>
        _attachments.CompleteAsync(session, request, cancellationToken);
}

/// <summary>POST /api/attachments/uploads/cancel — throws away one of your uploads that has not been sent.</summary>
public sealed class CancelUploadEndpoint : IAuthenticatedTypedEndpoint<UploadReference, UploadCancelled>
{
    private readonly AttachmentService _attachments;

    public CancelUploadEndpoint(AttachmentService attachments) => _attachments = attachments;

    public string Route => "/api/attachments/uploads/cancel";
    public HttpMethod Method => HttpMethod.Post;

    public async Task<UploadCancelled> HandleAsync(UserSession session, UploadReference request, CancellationToken cancellationToken)
    {
        await _attachments.CancelAsync(session, request.AttachmentId, cancellationToken);
        return new UploadCancelled(request.AttachmentId);
    }
}

/// <summary>
/// POST /api/attachments/download — an attachment's encrypted bytes, for anybody who can see the channel
/// of the message it was sent in. The body names it (<c>{ "attachmentId": … }</c>), so the id stays out of
/// the URL; a single <c>Range: bytes=start-end</c> is honoured, so an interrupted download can resume.
/// The bytes are ciphertext: the key is inside the message.
/// </summary>
public sealed class DownloadAttachmentEndpoint : IAuthenticatedEndpoint
{
    private readonly AttachmentService _attachments;
    private readonly PayloadCodecs _codecs;

    public DownloadAttachmentEndpoint(AttachmentService attachments, PayloadCodecs codecs)
    {
        _attachments = attachments;
        _codecs = codecs;
    }

    public string Route => "/api/attachments/download";
    public HttpMethod Method => HttpMethod.Post;

    public async Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken)
    {
        Stream content;
        long length;

        try
        {
            var request = await context.ReadPayloadAsync<UploadReference>(_codecs.ForRequest(context.Request), cancellationToken);
            (_, content) = await _attachments.OpenDownloadAsync(session, request.AttachmentId, cancellationToken);
            length = content.Length;
        }
        catch (ApiException ex)
        {
            await ex.WriteToAsync(context, cancellationToken);
            return;
        }

        await using (content)
        {
            var response = context.Response;
            response.Headers.AcceptRanges = "bytes";
            response.Headers.CacheControl = "no-store";
            response.ContentType = "application/octet-stream";

            var (start, end) = (0L, length - 1);

            if (context.Request.Headers.Range.ToString() is { Length: > 0 } range)
            {
                if (!TryParseRange(range, length, out start, out end))
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = $"bytes */{length}";
                    return;
                }

                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
            }

            var count = length == 0 ? 0 : end - start + 1;
            response.ContentLength = count;

            if (count == 0)
                return;

            content.Seek(start, SeekOrigin.Begin);
            await CopyAsync(content, response.Body, count, cancellationToken);
        }
    }

    /// <summary>
    /// Reads one <c>bytes=start-end</c>, <c>bytes=start-</c> or <c>bytes=-suffix</c> range. Several
    /// ranges at once are not offered: nothing downloading a file needs them.
    /// </summary>
    public static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
            return false;

        var spec = header["bytes=".Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix == 0)
                return false;

            start = Math.Max(0, length - suffix);
            return length > 0;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
            return false;

        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
                return false;

            end = Math.Min(end, length - 1);
        }

        return true;
    }

    private static async Task CopyAsync(Stream source, Stream target, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];

        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            count -= read;
        }
    }
}
