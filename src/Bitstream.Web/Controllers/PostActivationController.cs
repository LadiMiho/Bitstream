using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>
/// Post-Activation Support (TRD §6) landing page. Placeholder until this module's real screens
/// are built — <see cref="TicketsController"/> and <see cref="ServiceChangesController"/> already
/// have their JSON actions, just no page consuming them yet.
/// </summary>
[Route("PostActivation")]
public sealed class PostActivationController : Controller
{
    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Post-Activation Support";
        return View();
    }
}
