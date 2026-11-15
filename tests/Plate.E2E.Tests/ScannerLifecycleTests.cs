using FluentAssertions;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

public class ScannerLifecycleTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.Chromium, true)]
    [InlineData(E2EFixture.WebKit, true)]
    public async Task Delayed_camera_permission_after_teardown_stops_every_track(string browser, bool navigate)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Lunch&tab=barcode");
        await phone.Page.EvaluateAsync("""
            () => {
                const probe = window.cameraProbe = { stops: 0, plays: 0 };
                const stream = new MediaStream();
                stream.getTracks = () => [1, 2].map(() => ({ stop() { probe.stops++; } }));
                document.querySelector('#scanner video').play = async () => { probe.plays++; };
                // On the prototype: WebKit keeps the navigator.mediaDevices wrapper only while something listens for
                // devicechange, so a stub set on the instance is lost with it at the next garbage collection.
                Object.defineProperty(MediaDevices.prototype, 'getUserMedia', {
                    value: () => new Promise(resolve => { probe.grant = () => resolve(stream); })
                });
            }
            """);
        // The permission prompt is controlled by the probe, so exercise the delegated click directly.
        await phone.Page.Locator("[data-start]").EvaluateAsync("button => button.click()");
        await phone.Page.WaitForFunctionAsync("() => !!window.cameraProbe.grant", null, new() { Timeout = 5000 });

        if (navigate)
        {
            await phone.NavigateByAsync(() => phone.Page.EvaluateAsync("() => window.gymNav('/food')"));
            new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        }
        else
        {
            await phone.TapAsync("#scanner [data-sheet-close]");
            await phone.Page.Locator("#scanner").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        }

        var counters = await phone.Page.EvaluateAsync<int[]>("""
            async () => {
                const probe = window.cameraProbe;
                probe.grant();
                await new Promise(resolve => setTimeout(resolve, 0));
                return [probe.stops, probe.plays];
            }
            """);
        counters.Should().Equal(2, 0);
        phone.ShouldHaveNoErrors();
    }
}
