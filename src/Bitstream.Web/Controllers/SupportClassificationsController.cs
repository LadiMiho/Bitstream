using System.Globalization;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Presentation;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// The three-level classification of post-activation requests (portal.SupportClassification):
/// page, drawers and the JSON actions <c>wwwroot/js/pages/support-classifications.js</c> calls,
/// all gated on <c>catalogue.manage</c> — mirrors <see cref="CatalogueController"/>.
/// </summary>
[Route("PostActivation/Classifications")]
public sealed class SupportClassificationsController : Controller
{
    private readonly ISupportClassificationService _service;

    public SupportClassificationsController(ISupportClassificationService service) => _service = service;

    [HttpGet("")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> Index([FromQuery] long? l1, [FromQuery] long? l2, CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Support classifications";
        var tree = await _service.GetTreeAsync(cancellationToken).ConfigureAwait(false);

        return View(SupportClassificationsViewModel.Create(tree, l1, l2));
    }

    [HttpGet("AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> AddDrawer([FromQuery] long? parentId, CancellationToken cancellationToken)
    {
        var tree = await _service.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SupportClassificationNode> siblings = tree;
        Bitstream.Domain.Entities.SupportClassification? parent = null;

        if (parentId is { } id)
        {
            var parentNode = Flatten(tree).FirstOrDefault(n => n.Classification.ClassificationId == id);
            if (parentNode is null)
            {
                return NotFound();
            }

            parent = parentNode.Classification;
            siblings = parentNode.Children;
        }

        var nextOrder = siblings.Count == 0 ? 10 : (siblings.Max(s => s.Classification.DisplayOrder) / 10 * 10) + 10;

        return PartialView("_AddDrawer", new SupportClassificationAddViewModel(parent, SuggestCode(parent, siblings), nextOrder));
    }

    [HttpGet("{id:long}/EditDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> EditDrawer(long id, CancellationToken cancellationToken)
    {
        var classification = await _service.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return classification is null ? NotFound() : PartialView("_EditDrawer", classification);
    }

    [HttpPost("")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> Create([FromBody] CreateSupportClassificationHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var created = await _service.CreateAsync(request.ParentId, request.Code, request.Name, request.DisplayOrder, cancellationToken).ConfigureAwait(false);
            return Ok(new { classificationId = created.ClassificationId, level = created.Level });
        });

    [HttpPut("{id:long}")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> Update(long id, [FromBody] UpdateSupportClassificationHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await _service.UpdateAsync(id, request.Code, request.Name, request.DisplayOrder, cancellationToken).ConfigureAwait(false);
            return NoContent();
        });

    [HttpPatch("{id:long}/status")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> SetActive(long id, [FromBody] SetActiveHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await _service.SetActiveAsync(id, request.IsActive, cancellationToken).ConfigureAwait(false);
            return NoContent();
        });

    /// <summary>Runs one write and maps its outcome: the action's result, 404 for an unknown id, 400 with field errors.</summary>
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SupportClassificationNotFoundException)
        {
            return NotFound();
        }
        catch (SupportClassificationValidationException exception)
        {
            return BadRequest(new ValidationProblemDetails(exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
        }
    }

    private static IEnumerable<SupportClassificationNode> Flatten(IEnumerable<SupportClassificationNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    /// <summary>
    /// The CRM codes follow a pattern — a child's code is its parent's plus a suffix, numbered
    /// upwards (430013 → 4300130010, 4300130011 …) — so the drawer proposes the next one. Only a
    /// suggestion: the field stays editable and the server checks uniqueness.
    /// </summary>
    private static string SuggestCode(Bitstream.Domain.Entities.SupportClassification? parent, IReadOnlyList<SupportClassificationNode> siblings)
    {
        var prefix = parent?.Code ?? string.Empty;
        var numbered = siblings
            .Select(s => s.Classification.Code)
            .Where(c => c.StartsWith(prefix, StringComparison.Ordinal) && c.Length > prefix.Length && c.Length <= 20)
            .Select(c => c[prefix.Length..])
            .Where(suffix => suffix.All(char.IsAsciiDigit))
            .ToList();

        if (numbered.Count == 0)
        {
            return string.Empty;
        }

        var width = numbered.Max(s => s.Length);
        var next = numbered.Where(s => s.Length == width).Max(s => decimal.Parse(s, CultureInfo.InvariantCulture)) + 1;
        var suffix = next.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

        return suffix.Length > width ? string.Empty : prefix + suffix;
    }
}
