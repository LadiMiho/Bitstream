using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>Reporting landing page. Placeholder until this module's real screens are built.</summary>
[Route("Reporting")]
public sealed class ReportingController : Controller
{
    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Reporting";
        return View();
    }
}
