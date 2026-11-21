// Compares the Bulgarian reference foods in the seed with the USDA foods of the same English name. A reference row's
// English name links it to a USDA food (and says raw or cooked); when the two disagree by more than 25% of energy,
// either the values are wrong (a bad source) or the English name is (another variant or state). The 2026-11-21 audit
// found 38 such rows, mostly from macroscope.top and spidersport.com; reports/reference-foods-audit.md lists them.
//   node tools/foods/audit-reference.mjs            prints the conflicts as a Markdown table
import path from "node:path";
import { readCsv, repo } from "./lib.mjs";

const seed = path.join(repo, "src/Plate.Web/Data/Seed");

/** The first word of a USDA-style name ("Lentils, raw" gives "lentils"). */
const head = (name) => name.toLowerCase().split(/[ ,(]/)[0];

/** Reference rows whose energy differs from their USDA twin's by more than 25% and 15 kcal, with nearby USDA variants. */
export function conflicts(bg = readCsv(path.join(seed, "bg-foods.csv")), usda = readCsv(path.join(seed, "usda-foods.csv"))) {
    const byName = new Map();
    for (const u of usda) {
        const key = u.name.toLowerCase();
        byName.set(key, [...(byName.get(key) || []), Number(u.kcal)]);
    }
    const result = [];
    for (const r of bg.filter((r) => r.kind === "generic" && r.name_en)) {
        const twin = byName.get(r.name_en.toLowerCase());
        if (!twin) continue;
        const usdaKcal = twin.reduce((a, b) => a + b, 0) / twin.length;
        const kcal = Number(r.kcal);
        if (Math.abs(kcal - usdaKcal) <= Math.max(0.25 * usdaKcal, 15)) continue;
        const variants = usda
            .filter((u) => head(u.name) === head(r.name_en) && Math.abs(Number(u.kcal) - kcal) <= 0.1 * Math.max(kcal, 1))
            .sort((a, b) => Math.abs(Number(a.kcal) - kcal) - Math.abs(Number(b.kcal) - kcal))
            .slice(0, 2)
            .map((u) => `${u.name} (${Number(u.kcal)})`);
        const source = (r.source.match(/https?:\/\/([^/\s]+)/) || [, "none"])[1];
        result.push({ name_bg: r.name_bg, name_en: r.name_en, kcal, usdaKcal: Math.round(usdaKcal), source, variants });
    }
    return result.sort((a, b) => Math.abs(b.kcal - b.usdaKcal) / b.usdaKcal - Math.abs(a.kcal - a.usdaKcal) / a.usdaKcal);
}

if (process.argv[1] && import.meta.url.endsWith(path.basename(process.argv[1]))) {
    const rows = conflicts();
    console.log("| Bulgarian name | English name (link) | kcal | USDA kcal | Source | USDA variants within 10% |");
    console.log("|-|-|-|-|-|-|");
    for (const r of rows) {
        console.log(`| ${r.name_bg} | ${r.name_en} | ${r.kcal} | ${r.usdaKcal} | ${r.source} | ${r.variants.join("; ") || "none"} |`);
    }
    console.error(`${rows.length} reference rows disagree with their USDA twin.`);
}
