using System.Security.Cryptography;
using System.Text;
using Bitstream.Application.Abstractions.Configuration;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Security;

/// <summary>
/// How CRM authenticates its calls to the inbound event API (<c>Integration:CrmInbound</c>): a
/// shared key in a request header. The key is <see cref="ApiKey"/> (appsettings.json, or the
/// environment variable <c>BITSTREAM_Integration__CrmInbound__ApiKey</c>); when that is empty it
/// falls back to the secret store, <c>Secrets:{ApiKeySecretName}</c>.
/// </summary>
public sealed class CrmInboundOptions
{
    public const string SectionName = "Integration:CrmInbound";

    /// <summary>Header CRM puts the key in.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>The key CRM must send. Takes precedence over <see cref="ApiKeySecretName"/> when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of the secret holding the expected key, used when <see cref="ApiKey"/> is empty.</summary>
    public string ApiKeySecretName { get; set; } = "CrmInboundApiKey";
}

/// <summary>
/// Rejects any call to the CRM inbound API without the configured API key, with 401. Fails
/// closed: when no key is configured, every call is refused and the misconfiguration is logged,
/// rather than the interface silently running open.
/// </summary>
public sealed class CrmApiKeyEndpointFilter : IEndpointFilter
{
    private readonly IOptionsMonitor<CrmInboundOptions> _options;
    private readonly ISecretResolver _secretResolver;
    private readonly ILogger<CrmApiKeyEndpointFilter> _logger;

    public CrmApiKeyEndpointFilter(
        IOptionsMonitor<CrmInboundOptions> options, ISecretResolver secretResolver, ILogger<CrmApiKeyEndpointFilter> logger)
    {
        _options = options;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var options = _options.CurrentValue;
        var httpContext = context.HttpContext;
        var expected = !string.IsNullOrEmpty(options.ApiKey)
            ? options.ApiKey
            : await _secretResolver.GetSecretAsync(options.ApiKeySecretName, httpContext.RequestAborted).ConfigureAwait(false);

        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogError(
                "CRM inbound API key is not configured (Integration:CrmInbound:ApiKey or secret {SecretName}); refusing the call.",
                options.ApiKeySecretName);
            return Unauthorized("The portal has no API key configured for CRM calls.");
        }

        var provided = httpContext.Request.Headers[options.ApiKeyHeader].ToString();

        if (string.IsNullOrEmpty(provided)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
        {
            _logger.LogWarning("CRM inbound call rejected: missing or invalid {Header}.", options.ApiKeyHeader);
            return Unauthorized($"Missing or invalid {options.ApiKeyHeader} header.");
        }

        return await next(context).ConfigureAwait(false);
    }

    private static IResult Unauthorized(string detail) =>
        Results.Problem(title: "Unauthorized", detail: detail, statusCode: StatusCodes.Status401Unauthorized);
}
