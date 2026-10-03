// For each food in dishes.txt: a firecrawl search that scrapes the top pages (excluding sites whose terms forbid
// reuse, and social media), then an LLM (OpenRouter, tools/foods/lib.mjs) reads the per-100 g values off each page. Values agreeing across sources
// are combined (median); every row keeps the URLs it came from. Writes raw/dishes.csv. Resumable.
import fs from "node:fs";
import path from "node:path";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { raw, here, llm, writeCsv, plausible, pool } from "./lib.mjs";
const run = promisify(execFile);

const EXCLUDE = ["bb-team.org", "facebook.com", "instagram.com", "youtube.com", "tiktok.com", "scribd.com", "pinterest.com"];
const dishes = fs.readFileSync(path.join(here, "dishes.txt"), "utf8").split("\n").map((l) => l.trim()).filter((l) => l && !l.startsWith("#"));
const dir = path.join(raw, "dishes");
fs.mkdirSync(dir, { recursive: true });
const slug = (s) => s.toLowerCase().replace(/[^a-zа-я0-9]+/gi, "-");

async function search(dish) {
    const file = path.join(dir, slug(dish) + ".json");
    if (fs.existsSync(file)) {
        return JSON.parse(fs.readFileSync(file, "utf8"));
    }
    const q = `${dish} калории хранителна стойност на 100 г белтъчини мазнини въглехидрати ${EXCLUDE.map((d) => "-site:" + d).join(" ")}`;
    await run("firecrawl", ["search", q, "--limit", "3", "--scrape", "--only-main-content", "--json", "-o", file], { timeout: 180000 });
    return JSON.parse(fs.readFileSync(file, "utf8"));
}

// Only the parts of a page around nutrition numbers, to keep prompts small.
function excerpt(md) {
    const lines = (md || "").split("\n");
    const keep = new Set();
    lines.forEach((l, i) => {
        if (/(kcal|ккал|калори|белтъч|протеин|мазнин|въглехидрат|energy)/i.test(l)) {
            for (let j = Math.max(0, i - 4); j <= Math.min(lines.length - 1, i + 4); j++) keep.add(j);
        }
    });
    return [...keep].sort((a, b) => a - b).map((i) => lines[i]).join("\n").slice(0, 6000);
}

const SYSTEM = `You read nutrition values off web pages. For each page, say whether it gives values for the named food
(the same food, not a different dish or a branded variant with a different recipe) and, if so, the values per 100 g
of the food as eaten. Convert per-portion values to per 100 g only when the page states the portion weight in grams.
Never estimate or invent numbers: if a value is not on the page, the page does not count.`;

const schema = {
    type: "array",
    items: {
        type: "object",
        properties: {
            url: { type: "string" },
            same_food: { type: "boolean" },
            kcal: { type: "number" }, protein: { type: "number" }, carbs: { type: "number" }, fat: { type: "number" },
        },
        required: ["url", "same_food"],
    },
};

const median = (xs) => { const s = [...xs].sort((a, b) => a - b); const m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; };
// Searches one at a time (each also scrapes three pages, and the plan allows two jobs), then reading four at a time.
const results = await pool(dishes, 1, async (dish) => {
    try {
        return await search(dish);
    } catch (e) {
        console.log(`search failed: ${dish}: ${(e.stderr || e.message || "").toString().slice(0, 200)}`);
        return null;
    }
});
const rows = (await pool(dishes, 4, async (dish, i) => {
    const result = results[i];
    if (!result) {
        return null;
    }
    const pages = (result.data?.web || []).filter((p) => p.markdown && !EXCLUDE.some((d) => p.url.includes(d)));
    if (!pages.length) {
        console.log(`no pages: ${dish}`);
        return null;
    }
    const read = await llm({
        system: SYSTEM,
        schema,
        prompt: `Food: ${dish}\n\n` + pages.map((p) => `PAGE ${p.url}\n${excerpt(p.markdown)}`).join("\n\n----\n\n"),
    });
    const good = (read || []).filter((r) => r.same_food && [r.kcal, r.protein, r.carbs, r.fat].every((x) => typeof x === "number") && plausible(r));
    if (!good.length) {
        console.log(`no usable values: ${dish}`);
        return null;
    }
    // Drop a source that disagrees with the others by more than 30 % on energy.
    const mid = median(good.map((g) => g.kcal));
    const agree = good.length > 2 ? good.filter((g) => Math.abs(g.kcal - mid) <= 0.3 * mid) : good;
    const spread = agree.length > 1 ? (Math.max(...agree.map((g) => g.kcal)) - Math.min(...agree.map((g) => g.kcal))) / mid : 0;
    const row = {
        name_bg: dish,
        kcal: Math.round(median(agree.map((g) => g.kcal))),
        protein: +median(agree.map((g) => g.protein)).toFixed(1),
        carbs: +median(agree.map((g) => g.carbs)).toFixed(1),
        fat: +median(agree.map((g) => g.fat)).toFixed(1),
        sources: agree.length,
        spread: +spread.toFixed(2),
        source: agree.map((g) => g.url).join(" "),
    };
    console.log(`${dish}: ${row.kcal} kcal from ${agree.length}`);
    return row;
})).filter(Boolean);
writeCsv(path.join(raw, "dishes.csv"), ["name_bg", "kcal", "protein", "carbs", "fat", "sources", "spread", "source"], rows);
console.log(`dishes: ${rows.length} of ${dishes.length}`);
