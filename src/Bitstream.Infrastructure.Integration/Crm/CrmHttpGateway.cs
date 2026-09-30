using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Integration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Integration.Crm;

/// <summary>
/// Configuration of the CRM adapter. Endpoints and timeouts are externalised (TR-ARC-06);
/// credentials are resolved from the secret store and never from a settings file (TR-SEC-28).
/// </summary>
public sealed class CrmOptions
{
    public const string SectionName = "Integration:Crm";

    /// <summary>Base address of the CRM service. Value per environment (TR-ARC-07).</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Per-call timeout; a timeout must never leave a record indeterminate (TR-INT-08).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retry budget for technical failures (TR-INT-04). Not read by this class — the outbox
    /// dispatcher owns the outbox-level retry budget (<c>OutboxDispatcherOptions.MaxAttempts</c>)
    /// so that one setting governs every target system's messages, not one per adapter. Reserved
    /// here for an in-adapter policy (e.g. a single immediate retry of a dropped connection)
    /// if one turns out to be worth adding once the real contract is known.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Total window over which the attempts are spread.</summary>
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Name of the secret-store entry holding the CRM credential.</summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Client certificate thumbprint, when the agreed method is mutual TLS.</summary>
    public string? ClientCertificateThumbprint { get; set; }

    /// <summary>
    /// Path probed by the health check, relative to <see cref="BaseAddress"/>. Configurable
    /// because the path is CRM's to choose and is not yet agreed (TR-ARC-05, TR-ARC-06).
    /// </summary>
    public string? HealthPath { get; set; }

    /// <summary>Timeout for the health probe. Short, so readiness stays responsive.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Business Partner creation (INT-CRM-01), a SOAP operation on its own endpoint.</summary>
    public CrmBusinessPartnerOptions BusinessPartner { get; set; } = new();
}

/// <summary>
/// The <c>CRM_BP_CREATE</c> SOAP operation's endpoint and the fixed codes it is sent with
/// (<c>Integration:Crm:BusinessPartner</c>).
/// </summary>
public sealed class CrmBusinessPartnerOptions
{
    /// <summary>Absolute SOAP endpoint URL. Unset means INT-CRM-01 fails as a retryable technical failure.</summary>
    public Uri? Endpoint { get; set; }

    public string OperationCode { get; set; } = "CRM_BP_CREATE";

    /// <summary>Sent as CUSTOMERTYPE.</summary>
    public string CustomerType { get; set; } = "O000";

    /// <summary>Sent as BP_CAT.</summary>
    public string BpCategory { get; set; } = "2";

    /// <summary>Sent as PARTNERTYPE.</summary>
    public string PartnerType { get; set; } = "O100";

    /// <summary>
    /// Logs the full SOAP request and CRM's raw response at Information level. Off by default:
    /// the envelope carries the ISP's email, NIPT and mobile, so enable it only where that is
    /// acceptable in the logs (Development).
    /// </summary>
    public bool LogMessages { get; set; }

    /// <summary>
    /// When set, every CRM_BP_CREATE request XML (exactly as sent) and CRM's raw response are
    /// appended to a daily text file, <c>crm-bp-soap-yyyyMMdd.log</c>, in this directory. A
    /// relative path resolves against the process's working directory (the project folder when
    /// run from Visual Studio). Unset (the default) writes nothing — the file carries the ISP's
    /// email, NIPT and mobile.
    /// </summary>
    public string? MessageLogDirectory { get; set; }
}

/// <summary>
/// HTTP adapter for CRM (TRD 7.1 INT-CRM-01, -02, -04, -06, -08, -09; TRD 7.3.1 Direction A).
/// <para>
/// Business Partner creation (INT-CRM-01) uses the real <c>CRM_BP_CREATE</c> SOAP operation
/// (<see cref="CrmBusinessPartnerSoap"/>). Activation ticket creation (INT-CRM-02), complaint
/// ticket creation (INT-CRM-04), comment replication (INT-CRM-06), closure decision (INT-CRM-08)
/// and service change (INT-CRM-09) still use the provisional JSON payload shape in TRD §7.4,
/// since the rest of the CRM contract is still TRD 11.4 open item 1. Only
/// <c>FindTicketByIdempotencyKeyAsync</c> (the ambiguous-timeout status query, TR-INT-20) still
/// throws — there is nothing to poll without knowing what CRM's status response looks like.
/// </para>
/// <para>
/// When the real contract arrives: everything that needs to change is in this file — the
/// request/response shapes below and, if the auth scheme differs, <see cref="AuthorizeAsync"/>.
/// Nothing in the application or presentation layers has to change, because they only ever see
/// <see cref="ICrmGateway"/> and <see cref="IntegrationResult{TValue}"/>.
/// </para>
/// </summary>
public sealed class CrmHttpGateway : ICrmGateway
{
    private const string PendingContract =
        "CRM Direction A contract is not yet available (TRD 11.4 open item 1). " +
        "Configure a stub gateway in non-production environments.";

    /// <summary>Header idempotency travels on, in addition to the envelope's key already carried in the body (TR-INT-03, TR-INT-17).</summary>
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Serialises appends to the SOAP message log file across gateway instances.</summary>
    private static readonly SemaphoreSlim MessageLogLock = new(1, 1);

    private readonly HttpClient _client;
    private readonly CrmOptions _options;
    private readonly ISecretResolver _secretResolver;
    private readonly ILogger<CrmHttpGateway> _logger;

    public CrmHttpGateway(HttpClient httpClient, IOptions<CrmOptions> options, ISecretResolver secretResolver, ILogger<CrmHttpGateway> logger)
    {
        _client = httpClient;
        _options = options.Value;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    /// <summary>
    /// INT-CRM-01: creates the Business Partner through the <c>CRM_BP_CREATE</c> SOAP operation.
    /// Only <c>responseCode</c> 0 is success; any other code is a business rejection (not
    /// retried). CRM returns no separate customer ID, so <c>BP_NO</c> is used for both
    /// <see cref="CreateCrmCustomerResult.CrmCustomerId"/> and
    /// <see cref="CreateCrmCustomerResult.BusinessPartner"/>.
    /// </summary>
    public async Task<IntegrationResult<CreateCrmCustomerResult>> CreateCustomerAsync(
        CreateCrmCustomerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var bpOptions = _options.BusinessPartner;

        if (bpOptions.Endpoint is null)
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(
                "Integration:Crm:BusinessPartner:Endpoint is not configured.");
        }

        var envelope = CrmBusinessPartnerSoap.BuildCreateRequest(bpOptions, command);

        if (bpOptions.LogMessages)
        {
            _logger.LogInformation(
                "CRM_BP_CREATE request for {RequestPublicId} to {Endpoint}: {SoapRequest}",
                command.RequestPublicId, bpOptions.Endpoint, envelope);
        }

        await WriteMessageLogAsync(command.RequestPublicId, $"REQUEST POST {bpOptions.Endpoint}", envelope, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, bpOptions.Endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");

        HttpResponseMessage response;

        try
        {
            response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeout = $"CRM Business Partner creation did not respond within {_options.Timeout.TotalSeconds:F0}s.";
            await WriteMessageLogAsync(command.RequestPublicId, "NO RESPONSE (timeout)", timeout, CancellationToken.None).ConfigureAwait(false);
            return IntegrationResult<CreateCrmCustomerResult>.Timeout(timeout);
        }
        catch (HttpRequestException exception)
        {
            await WriteMessageLogAsync(command.RequestPublicId, "NO RESPONSE (connection failed)", exception.Message, CancellationToken.None).ConfigureAwait(false);
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(exception.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);

            await WriteMessageLogAsync(command.RequestPublicId, $"RESPONSE HTTP {statusCode}", body, cancellationToken).ConfigureAwait(false);

            if (bpOptions.LogMessages)
            {
                _logger.LogInformation(
                    "CRM_BP_CREATE response for {RequestPublicId} (HTTP {StatusCode}): {SoapResponse}",
                    command.RequestPublicId, statusCode, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = CrmBusinessPartnerSoap.TryReadFault(body)
                    ?? $"CRM Business Partner creation returned {(int)response.StatusCode} {response.ReasonPhrase}.";

                return IsBusinessRejection(response.StatusCode)
                    ? IntegrationResult<CreateCrmCustomerResult>.BusinessRejection(statusCode, detail)
                    : IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(detail, statusCode);
            }

            return CrmBusinessPartnerSoap.ParseCreateResponse(body);
        }
    }

    public async Task<IntegrationResult<CreateCrmTicketResult>> CreateActivationTicketAsync(
        CreateActivationTicketCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new ActivationTicketRequestBody(
            command.RequestPublicId, command.CrmCustomerId, command.BusinessPartner, command.Classification, command.PackageCode,
            command.ContractDurationMonths, command.LocationRaw, command.LocationLat, command.LocationLng, command.Comments);

        return await SendAsync(
            "tickets", command.Envelope.IdempotencyKey, body,
            (TicketResponseBody response) => new CreateCrmTicketResult(response.CrmTicketId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntegrationResult<CreateCrmTicketResult>> CreateComplaintTicketAsync(
        CreateComplaintTicketCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new ComplaintTicketRequestBody(
            command.TicketPublicId, command.BusinessPartner, command.ContractId, command.SubscriberReference,
            command.CategoryL1, command.CategoryL2, command.CategoryL3, command.Description);

        return await SendAsync(
            "complaint-tickets", command.Envelope.IdempotencyKey, body,
            (TicketResponseBody response) => new CreateCrmTicketResult(response.CrmTicketId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntegrationResult<ReplicateCommentResult>> ReplicateCommentAsync(
        ReplicateCommentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new CommentRequestBody(
            command.TicketPublicId, command.CrmTicketId, command.AuthorDisplayName, command.AuthorType, command.Body, command.CreatedAt);

        return await SendAsync(
            "comments", command.Envelope.IdempotencyKey, body,
            (CommentResponseBody response) => new ReplicateCommentResult(response.CrmCommentId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntegrationResult<ClosureDecisionResult>> SubmitClosureDecisionAsync(
        ClosureDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new ClosureDecisionRequestBody(
            command.TicketPublicId, command.CrmTicketId, command.Decision, command.SystemInitiated, command.SystemReason);

        return await SendAsync(
            "closure-decisions", command.Envelope.IdempotencyKey, body,
            (ClosureDecisionResponseBody response) => new ClosureDecisionResult(response.CrmTicketStatus),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntegrationResult<ServiceChangeResult>> SubmitServiceChangeAsync(
        ServiceChangeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new ServiceChangeRequestBody(
            command.ChangePublicId, command.ContractId, command.ChangeType, command.PackageAsIs,
            command.PackageToBe, command.RequestedTerminationDate);

        return await SendAsync(
            "service-changes", command.Envelope.IdempotencyKey, body,
            (ServiceChangeResponseBody response) => new ServiceChangeResult(response.CrmReference),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IntegrationResult<CreateCrmTicketResult>> FindTicketByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    /// <summary>
    /// One call shape for both operations: POST the command, add the idempotency header, map a
    /// 2xx body, map 4xx to a business rejection and everything else (5xx, a dropped connection,
    /// a timeout) to a technical failure or a timeout — the distinction TR-INT-19/-20 require.
    /// </summary>
    private async Task<IntegrationResult<TResult>> SendAsync<TBody, TResponse, TResult>(
        string path,
        string idempotencyKey,
        TBody body,
        Func<TResponse, TResult> map,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;

        try
        {
            response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // TR-INT-20: an ambiguous timeout is followed by an idempotent retry (the same
            // idempotency key), never a blind second create.
            return IntegrationResult<TResult>.Timeout($"CRM did not respond within {_options.Timeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException exception)
        {
            return IntegrationResult<TResult>.TechnicalFailure(exception.Message);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var parsed = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);

                return parsed is null
                    ? IntegrationResult<TResult>.TechnicalFailure("CRM returned a success status with an unreadable body.")
                    : IntegrationResult<TResult>.Success(map(parsed));
            }

            var detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);

            // TR-INT-19: 4xx is CRM refusing the request on business grounds (bad data, a
            // duplicate it does not recognise as one, a rule the portal did not enforce) and
            // must not be retried; anything else — 5xx, 429 — is transient (TR-INT-04).
            return IsBusinessRejection(response.StatusCode)
                ? IntegrationResult<TResult>.BusinessRejection(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), detail)
                : IntegrationResult<TResult>.TechnicalFailure(detail, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Appends one entry to the daily SOAP message log when
    /// <see cref="CrmBusinessPartnerOptions.MessageLogDirectory"/> is set. A failure to write is
    /// logged and swallowed — diagnostics must never fail the CRM call itself.
    /// </summary>
    private async Task WriteMessageLogAsync(string requestPublicId, string heading, string content, CancellationToken cancellationToken)
    {
        var directory = _options.BusinessPartner.MessageLogDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var path = Path.GetFullPath(Path.Combine(directory, $"crm-bp-soap-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"));
        var entry =
            $"===== {now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} UTC | {requestPublicId} | {heading} ====={Environment.NewLine}" +
            $"{content}{Environment.NewLine}{Environment.NewLine}";

        await MessageLogLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, entry, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not write the CRM_BP_CREATE message log to {Path}.", path);
        }
        finally
        {
            MessageLogLock.Release();
        }
    }

    private static bool IsBusinessRejection(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity;

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<CrmErrorBody>(cancellationToken).ConfigureAwait(false);
            return problem?.Message ?? $"CRM returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (JsonException)
        {
            return $"CRM returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
    }

    /// <summary>
    /// Bearer token, resolved fresh per call rather than cached on the client — the auth
    /// scheme itself is provisional (TRD 11.4 open item 1); this is the one place it changes.
    /// </summary>
    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CredentialSecretName))
        {
            return;
        }

        var credential = await _secretResolver.GetSecretAsync(_options.CredentialSecretName, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
    }

    // --- Provisional TRD 7.4 payload shape --------------------------------------------------

    private sealed record ActivationTicketRequestBody(
        [property: JsonPropertyName("requestPublicId")] string RequestPublicId,
        [property: JsonPropertyName("crmCustomerId")] string CrmCustomerId,
        [property: JsonPropertyName("businessPartner")] string BusinessPartner,
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("packageCode")] string PackageCode,
        [property: JsonPropertyName("contractDurationMonths")] int ContractDurationMonths,
        [property: JsonPropertyName("locationRaw")] string LocationRaw,
        [property: JsonPropertyName("locationLat")] decimal LocationLat,
        [property: JsonPropertyName("locationLng")] decimal LocationLng,
        [property: JsonPropertyName("comments")] string? Comments);

    private sealed record TicketResponseBody([property: JsonPropertyName("crmTicketId")] string CrmTicketId);

    private sealed record ComplaintTicketRequestBody(
        [property: JsonPropertyName("ticketPublicId")] string TicketPublicId,
        [property: JsonPropertyName("businessPartner")] string BusinessPartner,
        [property: JsonPropertyName("contractId")] string ContractId,
        [property: JsonPropertyName("subscriberReference")] string SubscriberReference,
        [property: JsonPropertyName("categoryL1")] string CategoryL1,
        [property: JsonPropertyName("categoryL2")] string CategoryL2,
        [property: JsonPropertyName("categoryL3")] string CategoryL3,
        [property: JsonPropertyName("description")] string Description);

    private sealed record CommentRequestBody(
        [property: JsonPropertyName("ticketPublicId")] string TicketPublicId,
        [property: JsonPropertyName("crmTicketId")] string CrmTicketId,
        [property: JsonPropertyName("authorDisplayName")] string AuthorDisplayName,
        [property: JsonPropertyName("authorType")] string AuthorType,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

    private sealed record CommentResponseBody([property: JsonPropertyName("crmCommentId")] string CrmCommentId);

    private sealed record ClosureDecisionRequestBody(
        [property: JsonPropertyName("ticketPublicId")] string TicketPublicId,
        [property: JsonPropertyName("crmTicketId")] string CrmTicketId,
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("systemInitiated")] bool SystemInitiated,
        [property: JsonPropertyName("systemReason")] string? SystemReason);

    private sealed record ClosureDecisionResponseBody([property: JsonPropertyName("crmTicketStatus")] string CrmTicketStatus);

    private sealed record ServiceChangeRequestBody(
        [property: JsonPropertyName("changePublicId")] string ChangePublicId,
        [property: JsonPropertyName("contractId")] string ContractId,
        [property: JsonPropertyName("changeType")] string ChangeType,
        [property: JsonPropertyName("packageAsIs")] string PackageAsIs,
        [property: JsonPropertyName("packageToBe")] string? PackageToBe,
        [property: JsonPropertyName("requestedTerminationDate")] DateOnly? RequestedTerminationDate);

    private sealed record ServiceChangeResponseBody([property: JsonPropertyName("crmReference")] string CrmReference);

    private sealed record CrmErrorBody([property: JsonPropertyName("message")] string? Message);
}
