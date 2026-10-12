using System.Text.Json.Serialization;
using Bitstream.Hosting.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>A browser-side failure reported by <c>wwwroot/js/api-client.js</c>'s <c>reportClientError</c>.</summary>
public sealed record ClientErrorReport(
    [property: JsonPropertyName("page")] string? Page,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>
/// Puts errors that happen in the browser into the server log (and so the log file): a request
/// that never got an answer, or an answer the page could not use, never reaches the server as
/// an error of its own. Anonymous, because the login page needs it before anyone is signed in;
/// rate-limited and length-capped, and the scripts only ever send the error itself, never what
/// was typed into a form.
/// </summary>
[Route("ClientLog")]
public sealed class ClientLogController : Controller
{
    private const int MaxFieldLength = 500;

    private readonly ILogger<ClientLogController> _logger;

    public ClientLogController(ILogger<ClientLogController> logger) => _logger = logger;

    [HttpPost("")]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    [RequestSizeLimit(8 * 1024)]
    [IgnoreAntiforgeryToken]
    public IActionResult Report([FromBody] ClientErrorReport? report)
    {
        if (report is null)
        {
            return BadRequest();
        }

        _logger.LogWarning(
            "Client error on {Page}: {Message} (HTTP {Status}, {Url}) {Detail}",
            Clean(report.Page) ?? "(unknown page)",
            Clean(report.Message) ?? "(no message)",
            report.Status ?? 0,
            Clean(report.Url) ?? string.Empty,
            Clean(report.Detail) ?? string.Empty);

        return NoContent();
    }

    /// <summary>Single line, capped: a report must not be able to forge extra log lines or flood the file.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = value.ReplaceLineEndings(" ").Trim();

        return singleLine.Length <= MaxFieldLength ? singleLine : singleLine[..MaxFieldLength] + "…";
    }
}
