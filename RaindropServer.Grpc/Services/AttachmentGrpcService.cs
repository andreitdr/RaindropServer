using Google.Protobuf;
using Grpc.Core;
using RaindropServer.Attachments;
using RaindropServer.Grpc.Mapping;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Services;

/// <summary>
/// Encrypted files; every rule is <see cref="AttachmentService"/>'s. Uploads and downloads stream in
/// frames of <see cref="FrameBytes"/>, well under gRPC's message limit.
/// </summary>
public sealed class AttachmentGrpcService : Proto.AttachmentService.AttachmentServiceBase
{
    /// <summary>The most ciphertext one upload or download frame carries.</summary>
    public const int FrameBytes = 256 * 1024;

    private readonly AttachmentService _attachments;
    private readonly AttachmentOptions _options;

    public AttachmentGrpcService(AttachmentService attachments, AttachmentOptions options)
    {
        _attachments = attachments;
        _options = options;
    }

    public override Task<Proto.GetAttachmentPolicyResponse> GetAttachmentPolicy(
        Proto.GetAttachmentPolicyRequest request, ServerCallContext context) =>
        Task.FromResult(new Proto.GetAttachmentPolicyResponse { Policy = _attachments.GetPolicy().ToProto(FrameBytes) });

    public override async Task<Proto.CreateUploadResponse> CreateUpload(
        Proto.CreateUploadRequest request, ServerCallContext context)
    {
        var status = await _attachments.CreateUploadAsync(context.Session(),
            new CreateUploadRequest(
                Id(request.ChannelId, "channel_id"),
                request.Extension,
                request.CiphertextSize,
                AttachmentKind(request.Kind)),
            context.CancellationToken);

        return new Proto.CreateUploadResponse { Status = status.ToProto(FrameBytes) };
    }

    /// <summary>
    /// Writes the stream into the upload as a run of chunks of at most
    /// <see cref="AttachmentOptions.UploadChunkBytes"/>, each committed as it completes. A stream that
    /// breaks keeps every committed chunk; the client resumes from <see cref="GetUploadStatus"/>.
    /// </summary>
    public override async Task<Proto.UploadResponse> Upload(
        IAsyncStreamReader<Proto.UploadRequest> requestStream, ServerCallContext context)
    {
        var session = context.Session();
        var cancellationToken = context.CancellationToken;

        if (!await requestStream.MoveNext(cancellationToken)
            || requestStream.Current.FrameCase != Proto.UploadRequest.FrameOneofCase.Header)
            throw ApiException.BadRequest("invalid_frame", "An upload stream starts with its header.");

        var header = requestStream.Current.Header;
        var attachmentId = Id(header.AttachmentId, "attachment_id");
        var offset = header.Offset;
        var frames = new UploadFrames(requestStream, cancellationToken);

        UploadStatus? status = null;

        while (await frames.HasMoreAsync())
        {
            status = await _attachments.WriteChunkAsync(
                session, attachmentId, offset, frames.NextChunk(_options.UploadChunkBytes), cancellationToken);
            offset = status.ReceivedSize;
        }

        // A header with no data writes nothing; it still answers where the upload stands.
        status ??= await _attachments.GetStatusAsync(session, attachmentId, cancellationToken);

        return new Proto.UploadResponse { Status = status.ToProto(FrameBytes) };
    }

    public override async Task<Proto.GetUploadStatusResponse> GetUploadStatus(
        Proto.GetUploadStatusRequest request, ServerCallContext context) =>
        new()
        {
            Status = (await _attachments.GetStatusAsync(context.Session(),
                Id(request.AttachmentId, "attachment_id"), context.CancellationToken)).ToProto(FrameBytes)
        };

    public override async Task<Proto.CompleteUploadResponse> CompleteUpload(
        Proto.CompleteUploadRequest request, ServerCallContext context) =>
        new()
        {
            Status = (await _attachments.CompleteAsync(context.Session(),
                new CompleteUploadRequest(Id(request.AttachmentId, "attachment_id"), request.CiphertextSha256.ToByteArray()),
                context.CancellationToken)).ToProto(FrameBytes)
        };

    public override async Task<Proto.CancelUploadResponse> CancelUpload(
        Proto.CancelUploadRequest request, ServerCallContext context)
    {
        var attachmentId = Id(request.AttachmentId, "attachment_id");
        await _attachments.CancelAsync(context.Session(), attachmentId, context.CancellationToken);

        return new Proto.CancelUploadResponse { AttachmentId = Text(attachmentId) };
    }

    public override async Task Download(
        Proto.DownloadRequest request, IServerStreamWriter<Proto.DownloadResponse> responseStream, ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;
        var (_, content) = await _attachments.OpenDownloadAsync(
            context.Session(), Id(request.AttachmentId, "attachment_id"), cancellationToken);

        await using (content)
        {
            var length = content.Length;

            if (request.Offset < 0 || request.Offset > length)
                throw ApiException.BadRequest("invalid_offset",
                    $"This file is {length} bytes; a download starts between 0 and that.");

            await responseStream.WriteAsync(new Proto.DownloadResponse
            {
                Header = new Proto.DownloadHeader { CiphertextSize = length, Offset = request.Offset }
            }, cancellationToken);

            content.Seek(request.Offset, SeekOrigin.Begin);

            var buffer = new byte[FrameBytes];
            int read;

            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                await responseStream.WriteAsync(
                    new Proto.DownloadResponse { Data = ByteString.CopyFrom(buffer, 0, read) }, cancellationToken);
        }
    }

    /// <summary>
    /// The data frames of an upload stream, read as a sequence of bounded <see cref="Stream"/>s — the
    /// shape <see cref="AttachmentService.WriteChunkAsync"/> already takes, so every rule it applies to a
    /// chunk applies here unchanged. A frame may straddle two chunks.
    /// </summary>
    private sealed class UploadFrames(IAsyncStreamReader<Proto.UploadRequest> requests, CancellationToken cancellationToken)
    {
        private ReadOnlyMemory<byte> _pending;
        private bool _ended;

        /// <summary>Whether any data is left, reading the next frame if the last one is used up.</summary>
        public async Task<bool> HasMoreAsync()
        {
            while (_pending.IsEmpty && !_ended)
            {
                if (!await requests.MoveNext(cancellationToken))
                {
                    _ended = true;
                    break;
                }

                var frame = requests.Current;

                if (frame.FrameCase != Proto.UploadRequest.FrameOneofCase.Data)
                    throw ApiException.BadRequest("invalid_frame", "An upload stream has one header, first; everything after it is data.");

                if (frame.Data.Length > FrameBytes)
                    throw ApiException.TooLarge("chunk_too_large", $"An upload frame carries at most {FrameBytes} bytes.");

                _pending = frame.Data.Memory;
            }

            return !_pending.IsEmpty;
        }

        /// <summary>The next run of data, ending after <paramref name="limit"/> bytes or with the stream.</summary>
        public Stream NextChunk(long limit) => new Chunk(this, limit);

        private sealed class Chunk(UploadFrames frames, long limit) : Stream
        {
            private long _read;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (_read >= limit || !await frames.HasMoreAsync())
                    return 0;

                var count = (int)Math.Min(Math.Min(buffer.Length, frames._pending.Length), limit - _read);
                frames._pending[..count].CopyTo(buffer);
                frames._pending = frames._pending[count..];
                _read += count;

                return count;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
