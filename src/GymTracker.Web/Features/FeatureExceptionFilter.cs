using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace GymTracker.Web.Features;

public static class RateLimits
{
    public const string Login = "login";
}

/// <summary>Maps service exceptions to HTTP responses so controllers stay thin.</summary>
public class FeatureExceptionFilter(ITempDataDictionaryFactory tempDataFactory) : IExceptionFilter
{
    public const string ErrorKey = "Error";

    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case NotFoundException:
                context.Result = new NotFoundResult();
                context.ExceptionHandled = true;
                break;

            case ValidationException ex when IsHtmx(context.HttpContext.Request):
                // The layout's script shows the text of 400 responses as a toast.
                context.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    Content = ex.Message,
                    ContentType = "text/plain; charset=utf-8",
                };
                context.ExceptionHandled = true;
                break;

            case ValidationException ex:
                // Plain form post: go back to the page with the message shown by the layout.
                tempDataFactory.GetTempData(context.HttpContext)[ErrorKey] = ex.Message;
                var referer = context.HttpContext.Request.Headers.Referer.ToString();
                context.Result = new RedirectResult(IsLocal(referer, context.HttpContext.Request) ? referer : "/today");
                context.ExceptionHandled = true;
                break;
        }
    }

    private static bool IsHtmx(HttpRequest request) => request.Headers.ContainsKey("HX-Request");

    private static bool IsLocal(string url, HttpRequest request) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == request.Host.Host;
}
