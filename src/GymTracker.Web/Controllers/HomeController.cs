using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

public class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => Redirect("/today");

    [AllowAnonymous]
    [HttpGet("/error/{code:int?}")]
    [HttpPost("/error/{code:int?}")]
    [IgnoreAntiforgeryToken]
    public IActionResult Error(int? code) => View(code ?? 500);

    [HttpGet("/food")]
    public IActionResult Food() => View("ComingSoon", "Food logging arrives in Phase 3.");
}
