using FluentAssertions;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>One browser context at phone size, with the app's soft navigation, requests and console errors observable.</summary>
public sealed class Phone : IAsyncDisposable
{
    // Counts soft navigations (nav.js dispatches gym:page after each swap); a full load resets it with the page.
    private const string InitScript = """
        window.__gymPages = 0;
        document.addEventListener("gym:page", () => { window.__gymPages++; });
        // SettledAsync waits for this rather than Playwright's own DOMContentLoaded state (see there).
        document.addEventListener("DOMContentLoaded", () => { window.__gymLoaded = true; });
        // The app asks in its own sheet (SPEC 7); a native dialog would also block the test.
        window.confirm = () => { console.error("window.confirm was called"); return false; };
        """;

    private readonly E2EFixture app;

    public Phone(E2EFixture app, string browser, IBrowserContext context, IPage page)
    {
        this.app = app;
        Browser = browser;
        Context = context;
        Page = page;
        context.AddInitScriptAsync(InitScript).GetAwaiter().GetResult();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                Errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => Errors.Add(error);
        page.Request += (_, request) => Requests.Add($"{request.Method} {new Uri(request.Url).PathAndQuery}");
        page.Response += (_, response) => Responses.Add($"{response.Status} {new Uri(response.Url).PathAndQuery}");
        page.RequestFailed += (_, request) => Responses.Add($"FAILED {request.Failure} {new Uri(request.Url).PathAndQuery}");
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
            {
                Navigations.Add(frame.Url);
            }
        };
    }

    public string Browser { get; }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    public string Email { get; set; } = "";

    public List<string> Errors { get; } = [];

    public List<string> Requests { get; } = [];

    public List<string> Responses { get; } = [];

    /// <summary>Every URL the main frame navigated to, in order: full loads and the soft navigations' pushState alike.</summary>
    public List<string> Navigations { get; } = [];

    public async Task SignInAsync(string email, string password)
    {
        try
        {
            await Page.GotoAsync("/account/login");
        }
        catch (TimeoutException ex)
        {
            // Name what the page was still waiting for, so a hang can be traced.
            var finished = Responses.Count == 0 ? "none" : string.Join("\n", Responses);
            throw new TimeoutException($"{ex.Message}\nRequests:\n{string.Join("\n", Requests)}\nResponses:\n{finished}", ex);
        }

        await Page.FillAsync("#email", email);
        await Page.FillAsync("#password", password);
        await Page.ClickAsync("button[type=submit]");
        await Page.WaitForURLAsync(url => !url.Contains("/account/login"));
    }

    /// <summary>Opens a page with a full load.</summary>
    public async Task GoAsync(string path)
    {
        await Page.GotoAsync(path);
        await SettledAsync();
    }

    /// <summary>Runs <paramref name="action"/> and waits for the soft navigation or full load it causes to finish.</summary>
    /// <param name="path">
    /// Where the action leads, when known: the wait then also holds until the address has that path, so a navigation
    /// already under way when the action ran can't end it early. A WebKit run of the whole BarcodeFieldTests class
    /// (2026-11-21) once ended this wait on /food/add after tapping a link to /food/barcode; 23 runs of the class and
    /// 40 of the test alone didn't repeat it.
    /// </param>
    public async Task NavigateByAsync(Func<Task> action, string? path = null)
    {
        var before = await Page.EvaluateAsync<double[]>("() => [performance.timeOrigin, window.__gymPages || 0]");
        var requests = Requests.Count;
        await action();
        // Playwright's usual 30 s. GitHub's two-core runners now and then take 30 to 50 s over a test that takes 3 s
        // here, and at 10 s this wait failed a different WebKit test on two of four runs (2026-11-15).
        try
        {
            await Page.WaitForFunctionAsync(
                "([b, path]) => (performance.timeOrigin !== b[0] || (window.__gymPages || 0) > b[1]) && (path === null || location.pathname === path)",
                new object?[] { before, path }, new() { Timeout = 30_000 });
        }
        catch (TimeoutException ex)
        {
            // A WebKit run on the Mac (2026-11-20) ran out here after tapping a form's submit with nothing to say why.
            throw await StuckAsync(ex, $"Before: timeOrigin {before[0]}, {before[1]} soft navigations. Requests since the action: {string.Join(", ", Requests.Skip(requests))}");
        }

        await SettledAsync();
    }

    /// <summary>Waits until the page is loaded and no view transition is running.</summary>
    /// <remarks>
    /// The page's own DOMContentLoaded flag (InitScript), not Page.WaitForLoadStateAsync. On a GitHub runner
    /// (2026-11-15) that wait ran out in WebKit right after GotoAsync had returned on "load": Playwright did not have
    /// DOMContentLoaded recorded for the page when the wait began, and only network idle arrived in the 30 s after.
    /// Why is not known (WebKit's first load in a fresh context swaps processes, which Playwright tracks separately). The flag asks the document itself, so lost bookkeeping can't stall the wait, and a page
    /// that really never finishes parsing still times out, now with what the page and network were doing.
    /// </remarks>
    public async Task SettledAsync()
    {
        try
        {
            await Page.WaitForFunctionAsync(
                "() => window.__gymLoaded === true && !document.documentElement.dataset.nav && !document.documentElement.classList.contains('is-navigating')");
        }
        catch (TimeoutException ex)
        {
            throw await StuckAsync(ex, null);
        }
    }

    /// <summary>A timeout with what the page and the network were doing, so a hang can be traced.</summary>
    private async Task<TimeoutException> StuckAsync(TimeoutException ex, string? detail)
    {
        string state;
        try
        {
            // Bounded: a page wedged enough to fail the wait may not answer this either.
            state = await Page.EvaluateAsync<string>(
                "() => JSON.stringify({ readyState: document.readyState, loaded: window.__gymLoaded === true, nav: document.documentElement.dataset.nav || null, navigating: document.documentElement.classList.contains('is-navigating'), timeOrigin: performance.timeOrigin, pages: window.__gymPages || 0, invalid: [...document.querySelectorAll(':invalid')].map(e => e.name || e.id).filter(Boolean) })")
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception stateError) when (stateError is PlaywrightException or TimeoutException)
        {
            state = $"unreadable: {stateError.Message}";
        }

        return new TimeoutException(
            $"{ex.Message}{(detail is null ? "" : "\n" + detail)}\nURL: {Page.Url}\nPage: {state}\nConsole errors:\n{string.Join("\n", Errors)}\nNavigations:\n{string.Join("\n", Navigations)}" +
            $"\nRequests:\n{string.Join("\n", Requests)}\nResponses:\n{string.Join("\n", Responses)}", ex);
    }

    public Task TapAsync(string selector) => Page.Locator(selector).First.TapAsync();

    /// <summary>Waits for the element's running animations (an entrance, say) so its box is where it will stay.</summary>
    public Task AnimationsDoneAsync(string selector) =>
        Page.Locator(selector).First.EvaluateAsync("e => Promise.all(e.getAnimations({ subtree: true }).map(a => a.finished))");

    public int Count(string requestPrefix) => Requests.Count(r => r.StartsWith(requestPrefix, StringComparison.Ordinal));

    public static async Task<LocatorBoundingBoxResult> BoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync() ?? throw new InvalidOperationException("Element is not visible.");

    public static bool Overlap(LocatorBoundingBoxResult a, LocatorBoundingBoxResult b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    // A console "Failed to load resource" doesn't say which; the failed responses are listed with it.
    public void ShouldHaveNoErrors() => Errors.Should().BeEmpty(
        $"the {Browser} console should stay clean (failed responses: {string.Join(", ", Responses.Where(r => r[0] is '4' or '5'))})");

    /// <summary>Where each phone saves a Playwright trace when it closes; set only on CI's retry of failed tests
    /// (tools/check.sh), so a test that fails twice can be replayed with <c>npx playwright show-trace</c>.</summary>
    public static readonly string? TraceDirectory =
        Environment.GetEnvironmentVariable("PLATE_E2E_TRACE_DIR") is { Length: > 0 } dir ? dir : null;

    public async ValueTask DisposeAsync()
    {
        if (TraceDirectory is not null)
        {
            var path = Page.IsClosed ? "closed" : new Uri(Page.Url).AbsolutePath.Trim('/').Replace('/', '-');
            await Context.Tracing.StopAsync(new()
            {
                Path = System.IO.Path.Combine(TraceDirectory, $"{Browser}-{path}-{Guid.NewGuid().ToString("N")[..8]}.zip"),
            });
        }
        await Context.CloseAsync();
    }
}
