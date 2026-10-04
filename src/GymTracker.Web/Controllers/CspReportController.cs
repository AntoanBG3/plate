using GymTracker.Web.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>Logs CSP violations reported by browsers while the policy is report-only.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("csp-report")]
public class CspReportController(ILogger<CspReportController> logger) : Controller
{
    [HttpPost("")]
    [EnableRateLimiting(RateLimits.CspReport)]
    [RequestSizeLimit(8 * 1024)]
    public async Task<IActionResult> Report(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        logger.LogWarning("CSP violation: {Report}", body.Length > 2000 ? body[..2000] : body);
        return NoContent();
    }
}
