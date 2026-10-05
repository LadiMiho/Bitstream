using System.Security.Cryptography;
using System.Text;
using Bitstream.Application.Abstractions.Configuration;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Security;

/// <summary>
/// How CRM authenticates its calls to the inbound event API (<c>Integration:CrmInbound</c>): a
/// shared secret in a request header. The key itself is never in a settings file outside
/// Development — it comes from the secret store, <c>Secrets:{ApiKeySecretName}</c> (environment
/// variable <c>BITSTREAM_Secrets__CrmInboundApiKey</c> by default), as every other credential does
/// (TR-SEC-28).
/// </summary>
public sealed class CrmInboundOptions
{
    public const string SectionName = "Integration:CrmInbound";

    /// <summary>Header CRM puts the key in.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>Name of the secret holding the expected key.</summary>
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
        var expected = await _secretResolver.GetSecretAsync(options.ApiKeySecretName, httpContext.RequestAborted).ConfigureAwait(false);

        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogError(
                "CRM inbound API key is not configured (secret {SecretName}); refusing the call.", options.ApiKeySecretName);
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
