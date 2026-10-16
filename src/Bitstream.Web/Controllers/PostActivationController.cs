using System.Text.Json;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// Post-Activation Support: the list of post-activation requests, the New request drawer (phone
/// number from BI, three linked classifications, note) and its submit, which queues CRM's
/// <c>CREATE_TICKET_BYPHONENR</c> (INT-CRM-11). Script: <c>wwwroot/js/pages/post-activation.js</c>.
/// </summary>
[Route("PostActivation")]
public sealed class PostActivationController : Controller
{
    private readonly IPostActivationRequestService _service;

    public PostActivationController(IPostActivationRequestService service) => _service = service;

    [HttpGet("")]
    [RequireSession]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Post-Activation Support";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, PostActivationPermissionCodes.TicketCreate);
        ViewBag.CanManageClassifications = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.CatalogueManage);
        ViewBag.ShowIsp = User.HasClaim(BitstreamClaimTypes.Permission, PostActivationPermissionCodes.TicketReadAll);

        var requests = await _service.SearchAsync(cancellationToken).ConfigureAwait(false);

        return View(requests);
    }

    [HttpGet("NewDrawer")]
    [RequirePermission(PostActivationPermissionCodes.TicketCreate)]
    public async Task<IActionResult> NewDrawer(CancellationToken cancellationToken)
    {
        var options = await _service.GetFormOptionsAsync(cancellationToken).ConfigureAwait(false);

        return PartialView("_NewDrawer", options);
    }

    [HttpGet("Requests/{publicId}/ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(string publicId, CancellationToken cancellationToken)
    {
        var details = await _service.GetAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (details is null)
        {
            return NotFound();
        }

        ViewBag.ShowRawData = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationReadAll);

        return PartialView("_ViewDrawer", details);
    }

    [HttpPost("")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreatePostActivationRequestHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _service.CreateAsync(request.Phonenumber, request.ClassificationId, request.Note, cancellationToken).ConfigureAwait(false);
            return Ok(new { publicId = created.PublicId });
        }
        catch (PostActivationRequestValidationException exception)
        {
            return BadRequest(new ValidationProblemDetails(exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
        }
    }

    /// <summary>The active classification tree as the drawer's script reads it: id, code, name, children.</summary>
    public static string ClassificationsJson(IReadOnlyList<SupportClassificationNode> nodes)
    {
        static object Map(SupportClassificationNode node) => new
        {
            id = node.Classification.ClassificationId,
            code = node.Classification.Code,
            name = node.Classification.Name,
            children = node.Children.Select(Map).ToList()
        };

        return JsonSerializer.Serialize(nodes.Select(Map).ToList());
    }
}
