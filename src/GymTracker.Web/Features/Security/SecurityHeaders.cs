namespace GymTracker.Web.Features.Security;

/// <summary>Baseline browser hardening on every response. The CSP is report-only until Security:CspEnforce is true.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration config)
{
    // 'wasm-unsafe-eval': the barcode ponyfill compiles ZXing's WebAssembly. Inline styles stay allowed (style="" in views).
    public const string Policy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; " +
        "manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    // Report-only until enforced: browsers then only report what the policy would block.
    private readonly (string Name, string Value) csp = config.GetValue("Security:CspEnforce", false)
        ? ("Content-Security-Policy", Policy)
        : ("Content-Security-Policy-Report-Only", Policy + "; report-uri /csp-report");

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h[csp.Name] = csp.Value;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!env.IsDevelopment())
            {
                // This host only; other peychev.uk subdomains are not covered.
                h.StrictTransportSecurity = "max-age=31536000";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}
