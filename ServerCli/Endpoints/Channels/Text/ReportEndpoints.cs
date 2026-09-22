using RaindropServer;
using RaindropServer.Channels.Text;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Text;

/// <summary>
/// POST /api/channels/text/messages/report — reports an end-to-end encrypted message, revealing its
/// text with the author's attestation, which the server verifies before accepting it.
/// </summary>
public sealed class ReportMessageEndpoint : IAuthenticatedTypedEndpoint<ReportMessageRequest, MessageReportSummary>
{
    private readonly MessageReportService _reports;

    public ReportMessageEndpoint(MessageReportService reports) => _reports = reports;

    public string Route => "/api/channels/text/messages/report";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageReportSummary> HandleAsync(
        UserSession session, ReportMessageRequest request, CancellationToken cancellationToken) =>
        _reports.ReportAsync(session, request, cancellationToken);
}

/// <summary>POST /api/channels/text/messages/reports — a channel's reports, for members with <c>manage_messages</c>.</summary>
public sealed class ListReportsEndpoint : IAuthenticatedTypedEndpoint<ListReportsRequest, ListReportsResponse>
{
    private readonly MessageReportService _reports;

    public ListReportsEndpoint(MessageReportService reports) => _reports = reports;

    public string Route => "/api/channels/text/messages/reports";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ListReportsResponse> HandleAsync(
        UserSession session, ListReportsRequest request, CancellationToken cancellationToken) =>
        _reports.ListAsync(session, request, cancellationToken);
}
