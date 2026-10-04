using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Features;

public static class ReturnUrls
{
    /// <summary>Where to go after signing in: the requested page if it is on this site, otherwise Today.</summary>
    public static string SafeReturnUrl(this IUrlHelper url, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : "/today";
}
