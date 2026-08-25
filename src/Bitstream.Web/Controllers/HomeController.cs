using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>Landing route. There is no dashboard of its own yet, so an authenticated visitor lands on the first module.</summary>
public sealed class HomeController : Controller
{
    [HttpGet("/")]
    [RequireSession]
    public IActionResult Index() => Redirect("/AccessManagement");
}
