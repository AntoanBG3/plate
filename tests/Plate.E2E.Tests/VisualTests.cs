using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Plate.Core.Domain;
using Plate.Core.Time;

namespace Plate.E2E.Tests;

/// <summary>
/// Buttons and text across the app's main screens, measured in the page (2026-11-23 audit): nothing scrolls sideways,
/// centred labels are centred, no button label wraps or is cut off by its container, text reaches 4.5:1 (3:1 when
/// large) and placeholders 3:1 against what is behind them, and back labels fit at 390 px. Dark and light, English
/// and Bulgarian, 320 and 390 px.
/// </summary>
public class VisualTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static string Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private const string Measure = """
        () => {
          const visible = (el) => {
            if (el.closest('[hidden], template')) return false;
            const cs = getComputedStyle(el), r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none' && Number(cs.opacity) > 0.05;
          };
          const path = (el) => { const p = []; for (let e = el; e && e !== document.body && p.length < 4; e = e.parentElement) p.unshift(e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.classList.length ? '.' + [...e.classList].slice(0, 3).join('.') : '')); return p.join(' > '); };
          const rgba = (s) => { const m = s.match(/rgba?\(([^)]+)\)/); if (!m) return null; const v = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return [v[0], v[1], v[2], v.length > 3 ? v[3] : 1]; };
          const lum = ([r, g, b]) => { const c = [r, g, b].map(x => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; };
          const blend = (top, bottom) => [0, 1, 2].map(i => top[i] * top[3] + bottom[i] * (1 - top[3])).concat([1]);
          const background = (el) => {
            const layers = [];
            for (let e = el; e; e = e.parentElement) {
              const cs = getComputedStyle(e);
              if (cs.backgroundImage && cs.backgroundImage !== 'none') return null; // a gradient or image: unknown
              const c = rgba(cs.backgroundColor); if (c && c[3] > 0) { layers.push(c); if (c[3] >= 1) break; }
            }
            let base = rgba(getComputedStyle(document.documentElement).backgroundColor) || [255, 255, 255, 1];
            if (base[3] < 1) base = blend(base, [255, 255, 255, 1]);
            for (const l of layers.reverse()) base = blend(l, base);
            return base;
          };
          const contrast = (fg, bg) => { const a = lum(fg) + 0.05, b = lum(bg) + 0.05; return Math.max(a, b) / Math.min(a, b); };
          const contentRects = (el) => {
            const rects = [];
            const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
            for (let n = walker.nextNode(); n; n = walker.nextNode()) {
              if (n.nodeType === 1) {
                if (!visible(n)) { continue; }
                if (n.tagName === 'svg' || n.tagName === 'IMG') rects.push(n.getBoundingClientRect());
                if (n.tagName === 'INPUT' && n.type !== 'hidden' && n.type !== 'checkbox' && n.type !== 'radio') rects.push(n.getBoundingClientRect());
                continue;
              }
              if (!n.textContent.trim() || !visible(n.parentElement)) continue;
              const range = document.createRange(); range.selectNodeContents(n);
              for (const r of range.getClientRects()) if (r.width > 0.5 && r.height > 0.5) rects.push(r);
            }
            return rects;
          };
          const buttons = [];
          for (const el of document.querySelectorAll('button, a.btn, .btn, [role=button], .segment, .mode-tab, .chip, .tab, .icon-btn, .sheet-close, label.btn, summary')) {
            if (!visible(el) || el.closest('.sr-only')) continue;
            const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
            const rects = contentRects(el);
            if (!rects.length) continue;
            const box = { l: r.left + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft), r: r.right - parseFloat(cs.borderRightWidth) - parseFloat(cs.paddingRight),
                          t: r.top + parseFloat(cs.borderTopWidth) + parseFloat(cs.paddingTop), b: r.bottom - parseFloat(cs.borderBottomWidth) - parseFloat(cs.paddingBottom) };
            const u = { l: Math.min(...rects.map(x => x.left)), r: Math.max(...rects.map(x => x.right)), t: Math.min(...rects.map(x => x.top)), b: Math.max(...rects.map(x => x.bottom)) };
            const textTops = [];
            const tw = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
            for (let t = tw.nextNode(); t; t = tw.nextNode()) {
              if (!t.textContent.trim() || !visible(t.parentElement)) continue;
              const range = document.createRange(); range.selectNodeContents(t);
              for (const r of range.getClientRects()) if (r.width > 0.5) textTops.push(Math.round((r.top + r.bottom) / 2));
            }
            const lineTops = new Set(textTops.sort((a, b) => a - b).filter((y, i, a) => i === 0 || y - a[i - 1] > 4));
            const centered = cs.textAlign === 'center' || (cs.display.includes('flex') && cs.justifyContent === 'center') || (cs.display.includes('grid') && (/center/.test(cs.justifyItems) || cs.justifyContent === 'center'));
            let clippedBy = null;
            for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
              const acs = getComputedStyle(a);
              if (!/hidden|clip/.test(acs.overflowX + acs.overflowY)) continue;
              const ar = a.getBoundingClientRect();
              if (r.right > ar.right + 1 || r.left < ar.left - 1) { clippedBy = path(a); break; }
            }
            buttons.push({ path: path(el), clippedBy, text: (el.innerText || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 60),
              w: Math.round(r.width), h: Math.round(r.height), lines: lineTops.size, centered,
              dx: +(((u.l + u.r) / 2) - ((box.l + box.r) / 2)).toFixed(1), dy: +(((u.t + u.b) / 2) - ((box.t + box.b) / 2)).toFixed(1),
              overflow: u.r > r.right + 1 || u.l < r.left - 1 || u.b > r.bottom + 1 || u.t < r.top - 1, disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true',
              fontSize: parseFloat(cs.fontSize) });
          }
          const texts = [];
          const seen = new Set();
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let n = walker.nextNode(); n; n = walker.nextNode()) {
            const el = n.parentElement;
            if (!n.textContent.trim() || !el || seen.has(el) || !visible(el) || el.closest('script, style, noscript, .sr-only, svg')) continue;
            seen.add(el);
            const cs = getComputedStyle(el), fg = rgba(cs.color), bg = background(el);
            const size = parseFloat(cs.fontSize), weight = Number(cs.fontWeight) || 400;
            const large = size >= 24 || (size >= 18.66 && weight >= 700);
            const disabled = !!el.closest('button:disabled, [aria-disabled=true], .is-disabled, input:disabled');
            const ratio = fg && bg ? contrast(fg[3] < 1 ? blend(fg, bg) : fg, bg) : null;
            const clipped = (cs.textOverflow === 'ellipsis' || cs.overflow === 'hidden' || cs.overflowX === 'hidden') && el.scrollWidth > el.clientWidth + 1;
            const r = el.getBoundingClientRect();
            texts.push({ path: path(el), text: n.textContent.trim().replace(/\s+/g, ' ').slice(0, 50), size, ratio: ratio && +ratio.toFixed(2),
              min: large ? 3 : 4.5, disabled, clipped, offscreen: r.right > innerWidth + 1 || r.left < -1 });
          }
          for (const el of document.querySelectorAll('input[placeholder], textarea[placeholder]')) {
            if (!visible(el) || el.value) continue;
            const ph = getComputedStyle(el, '::placeholder'), fg = rgba(ph.color), bg = background(el);
            if (fg && bg) texts.push({ path: path(el) + '::placeholder', text: el.placeholder.slice(0, 50), size: parseFloat(ph.fontSize), ratio: +contrast(fg[3] < 1 ? blend(fg, bg) : fg, bg).toFixed(2), min: 3, placeholder: true });
          }
          return { url: location.pathname + location.search, width: innerWidth, scrollWidth: document.documentElement.scrollWidth, buttons, texts };
        }
        """;

    private sealed record Button(string Path, string Text, int W, int H, int Lines, bool Centered, double Dx, double Dy, bool Overflow, bool Disabled, string? ClippedBy);
    private sealed record TextInfo(string Path, string Text, double Size, double? Ratio, double Min, bool Disabled, bool Clipped, bool Placeholder);
    private sealed record Page(string Url, int Width, int ScrollWidth, List<Button> Buttons, List<TextInfo> Texts);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Labels that wrap on purpose: two-line segment captions, the rest timer's label over its time.</summary>
    private static bool MayWrap(Button b) => b.Path.Split(" > ")[^1].Split('.').Contains("segment") || b.Path.Contains("rest-open") || b.Path.Contains("row");

    private static List<string> Problems(string name, Page p, int width)
    {
        var found = new List<string>();
        if (p.ScrollWidth > p.Width + 1) found.Add($"{name}: scrolls sideways ({p.ScrollWidth} > {p.Width})");
        foreach (var b in p.Buttons)
        {
            var label = $"{name}: \"{b.Text}\" ({b.Path.Split(" > ")[^1]})";
            if (b.Centered && Math.Abs(b.Dx) > 2) found.Add($"{label} is {b.Dx} px off centre");
            if (Math.Abs(b.Dy) > 2) found.Add($"{label} is {b.Dy} px off centre vertically");
            if (b.Lines > 1 && !MayWrap(b)) found.Add($"{label} wraps onto {b.Lines} lines");
            if (b.ClippedBy is not null) found.Add($"{label} is cut off by {b.ClippedBy}");
        }

        foreach (var t in p.Texts)
        {
            if (t.Ratio is { } ratio && ratio < t.Min && !t.Disabled)
                found.Add($"{name}: \"{t.Text}\" ({t.Path.Split(" > ")[^1]}) has contrast {ratio}, under {t.Min}");
            if (width >= 390 && t.Clipped && t.Path.Contains("topbar-back"))
                found.Add($"{name}: the back label \"{t.Text}\" is cut short");
        }

        return found;
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 320, Theme.Dark, true)]
    [InlineData(E2EFixture.Chromium, 320, Theme.Light, false)]
    [InlineData(E2EFixture.Chromium, 390, Theme.Light, true)]
    [InlineData(E2EFixture.Chromium, 390, Theme.Dark, false)]
    [InlineData(E2EFixture.WebKit, 390, Theme.Light, true)]
    public async Task Buttons_are_centred_unclipped_and_text_is_legible(string browser, int width, Theme theme, bool bulgarian)
    {
        var problems = new List<string>();
        async Task Check(Phone p, string name)
        {
            await p.Page.WaitForTimeoutAsync(400); // the page's own entrance motion settles
            problems.AddRange(Problems(name, (await p.Page.EvaluateAsync<JsonElement>(Measure)).Deserialize<Page>(Json)!, width));
        }

        await using (var anon = await app.PhoneAsync(browser, width, bulgarian ? "bg-BG" : "en-GB"))
        {
            await anon.GoAsync("/account/login");
            await Check(anon, "login");
        }

        await using var phone = await app.SignedInPhoneAsync(browser, width, bulgarian ? "bg-BG" : "en-GB");
        await phone.WithTargetsAsync(app, bulgarian);
        await using (var db = app.Db.CreateContext(null))
        {
            var userId = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
            await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == userId).ExecuteUpdateAsync(p => p.SetProperty(x => x.Theme, theme));
        }

        var templateId = await phone.CreateTemplateAsync(app, "Push A", "Bench press", "Overhead press");
        await phone.StartSessionAsync(templateId);
        await phone.LogSetAsync(phone.Page.Locator(".set-row").First, "60", "8");
        await phone.Page.WaitForTimeoutAsync(500);
        await Check(phone, "workout");

        foreach (var (name, path) in new[]
        {
            ("today", "/today"), ("train", "/train"), ("progress", "/progress"), ("settings", "/settings"),
            ("settings-goal", "/settings/goal"), ("settings-security", "/settings/security"), ("feedback-new", "/feedback/new"),
            ("add-search", $"/food/add?date={Today}&meal=Lunch&tab=search&q=banana"), ("add-manual", $"/food/add?date={Today}&meal=Lunch&tab=manual"),
            ("recipe-new", $"/food/recipes/new?date={Today}&meal=Lunch"),
        })
        {
            await phone.GoAsync(path);
            await Check(phone, name);
        }

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("150 g cooked rice, 1 banana");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.ClickAsync());
        await Check(phone, "check-foods");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/confirm'] button[type=submit]").ClickAsync());
        await Check(phone, "food");
        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=recent");
        await Check(phone, "add-recent");

        // BeEmpty's message names only the first item, which hid three more problems behind one on CI.
        problems.Should().BeEmpty("none should be found, and these were: {0}", string.Join(" | ", problems));
    }

    [Fact]
    public async Task Safari_on_a_Mac_shows_text_at_full_size()
    {
        // -apple-system-body is 17 px on an iPhone but 13 px in Safari on a Mac, where it shrank the whole app to three
        // quarters; Dynamic Type applies to touch screens only.
        await using var mac = await app.PhoneAsync(E2EFixture.WebKit, 1280, desktop: true);
        await mac.GoAsync("/account/login");

        (await mac.Page.EvaluateAsync<double>("() => parseFloat(getComputedStyle(document.documentElement).fontSize)")).Should().BeApproximately(17, 0.5);
    }
}
