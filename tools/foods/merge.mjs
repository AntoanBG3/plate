// Merges the collected Bulgarian foods with the USDA subset and removes duplicates across them.
//   node tools/foods/merge.mjs
// Inputs: raw/tables.csv, raw/dishes.csv, raw/off-bg.csv and the committed USDA seed.
// Output: src/Plate.Web/Data/Seed/bg-foods.csv (new foods) and raw/usda-bg-names.csv (Bulgarian names found
// for USDA foods that a Bulgarian source duplicated; the USDA translation pass uses them).
//
// Duplicates are decided by an LLM through OpenRouter on candidates found locally: a Bulgarian generic food is compared with
// the USDA foods and other Bulgarian rows sharing words with its English name. USDA wins over a duplicate (lab data),
// except over Bulgarian dishes, which are kept for their Bulgarian names;
// among Bulgarian sources the row backed by more sources wins. Branded products (with barcodes) are only
// de-duplicated by barcode. Hand-curated rows (lidl-bakery.csv, see lidl-bakery.md) are appended unchanged, so a
// re-run keeps them.
import fs from "node:fs";
import path from "node:path";
import { here, raw, repo, llm, readCsv, writeCsv, pool } from "./lib.mjs";

const seedDir = path.join(repo, "src/Plate.Web/Data/Seed");
const usda = readCsv(path.join(seedDir, "usda-foods.csv")).map((r) => ({ ...r, id: "u" + r.fdc_id }));
const tables = readCsv(path.join(raw, "tables.csv")).map((r) => ({ ...r, sources: 1, origin: "table" }));
const dishes = fs.existsSync(path.join(raw, "dishes.csv")) ? readCsv(path.join(raw, "dishes.csv")).map((r) => ({ ...r, origin: "dish" })) : [];
const off = readCsv(path.join(raw, "off-bg.csv"));

const generic = [...dishes, ...tables].map((r, i) => ({ ...r, id: "b" + i }));
console.log(`generic ${generic.length} (dishes ${dishes.length}, tables ${tables.length}), products ${off.length}, usda ${usda.length}`);

// ---- 1. English names for the Bulgarian generic foods ----
const englishFile = path.join(raw, "english-names.json");
const english = fs.existsSync(englishFile) ? JSON.parse(fs.readFileSync(englishFile, "utf8")) : {};
const needEnglish = [...new Set(generic.map((g) => g.name_bg))].filter((n) => !english[n]);
for (let i = 0; i < needEnglish.length; i += 80) {
    const batch = needEnglish.slice(i, i + 80);
    const out = await llm({
        system: "Give each Bulgarian food name the English name USDA FoodData Central would use for the same food, in USDA style (\"Cheese, feta\", \"Bread, rye\", \"Yogurt, plain, whole milk\"), including raw or cooked when it matters. For Bulgarian dishes with no USDA equivalent give a plain English description.",
        prompt: JSON.stringify(batch),
        schema: { type: "array", items: { type: "object", properties: { bg: { type: "string" }, en: { type: "string" } }, required: ["bg", "en"] } },
    }).catch((e) => (console.error(`\nenglish names batch failed: ${e.message}`), []));
    for (const o of out || []) english[o.bg] = o.en;
    fs.writeFileSync(englishFile, JSON.stringify(english, null, 1));
    process.stdout.write(`english ${Math.min(i + 80, needEnglish.length)}/${needEnglish.length}\r`);
}
console.log();

// ---- 2. Candidates by shared words ----
const words = (s) => (s || "").toLowerCase().replace(/[^a-zа-я0-9 ]+/gi, " ").split(/\s+/).filter((w) => w.length > 2 && !["raw", "with", "and", "the", "без", "със", "или"].includes(w));
const usdaIndex = new Map();
for (const u of usda) for (const w of new Set(words(u.name))) (usdaIndex.get(w) || usdaIndex.set(w, []).get(w)).push(u);

function usdaCandidates(g) {
    const ws = words(english[g.name_bg]);
    const score = new Map();
    for (const w of ws) for (const u of usdaIndex.get(w) || []) score.set(u, (score.get(u) || 0) + 1 / (1 + Math.log(1 + (usdaIndex.get(w).length))));
    return [...score.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6).map(([u]) => u);
}
function bgCandidates(g) {
    const ws = new Set(words(g.name_bg));
    const en = new Set(words(english[g.name_bg]));
    return generic.filter((o) => o.id !== g.id && (words(o.name_bg).some((w) => ws.has(w)) || words(english[o.name_bg]).filter((w) => en.has(w)).length >= 2)).slice(0, 6);
}

// ---- 3. The model decides ----
const decisionsFile = path.join(raw, "duplicates.json");
const decisions = fs.existsSync(decisionsFile) ? JSON.parse(fs.readFileSync(decisionsFile, "utf8")) : {};
const fmt = (x) => `${x.kcal} kcal, P ${x.protein}, C ${x.carbs}, F ${x.fat}`;
const todo = generic.filter((g) => !(g.id in decisions));
const SYSTEM = `You remove duplicate foods from a nutrition database. For each food, decide whether one of its candidates is
the same food: the same thing in the same state (raw/cooked, with or without skin, same fat level for dairy and meat),
so that a person logging it would not care which entry they pick, and the per-100 g values are close (energy within
about 15 %). A regional variant that differs in recipe or composition (Bulgarian кашкавал vs cheddar, бяло саламурено
сирене vs feta) is NOT a duplicate unless the values are nearly identical. Answer with the candidate id or null.`;
const batches = [];
for (let i = 0; i < todo.length; i += 25) batches.push(todo.slice(i, i + 25));
let judged = 0;
await pool(batches, 4, async (batch) => {
    const offered = new Map();
    const prompt = batch.map((g) => {
        const cands = [...usdaCandidates(g).map((u) => [u.id, `${u.id}: ${u.name} (${fmt(u)})`]), ...bgCandidates(g).map((o) => [o.id, `${o.id}: ${o.name_bg} [${english[o.name_bg]}] (${fmt(o)})`])];
        offered.set(g.id, new Set(cands.map(([id]) => id)));
        return `FOOD ${g.id}: ${g.name_bg} [${english[g.name_bg]}] (${fmt(g)})\nCANDIDATES:\n${cands.map(([, line]) => line).join("\n") || "(none)"}`;
    }).join("\n\n");
    const out = await llm({
        system: SYSTEM,
        prompt,
        schema: { type: "array", items: { type: "object", properties: { id: { type: "string" }, duplicate_of: { type: ["string", "null"] } }, required: ["id", "duplicate_of"] } },
    }).catch((e) => (console.error(`\nduplicate batch failed: ${e.message}`), []));
    // Only answers about foods in this batch that point at a candidate actually offered count.
    for (const o of out || []) {
        if (offered.has(o.id) && (!o.duplicate_of || offered.get(o.id).has(o.duplicate_of))) decisions[o.id] = o.duplicate_of || null;
    }
    fs.writeFileSync(decisionsFile, JSON.stringify(decisions, null, 1));
    judged += batch.length;
    process.stdout.write(`judged ${judged}/${todo.length}\r`);
});
console.log();
const undecided = generic.filter((g) => !(g.id in decisions)).length;
if (undecided > 0) {
    console.error(`${undecided} foods are still undecided; run again to retry them (decisions so far are cached). Nothing written.`);
    process.exit(1);
}

// ---- 4. Resolve: USDA wins; among Bulgarian rows the better-sourced one wins ----
const byId = new Map(generic.map((g) => [g.id, g]));
const rank = (g) => (g.origin === "dish" ? 10 : 0) + +g.sources;
const parent = new Map(generic.map((g) => [g.id, g.id]));
parent.set("U", "U"); // the root for every food that duplicates a USDA food
const find = (x) => (parent.get(x) === x ? x : find(parent.get(x)));
const usdaNames = new Map();
for (const g of generic) {
    const d = decisions[g.id];
    if (!d) continue;
    if (d.startsWith("u")) {
        // Bulgarian dishes stay even when USDA has something close: their Bulgarian names are what people search for.
        if (g.origin === "dish") continue;
        const u = usda.find((x) => x.id === d);
        if (u && !usdaNames.has(u.fdc_id)) usdaNames.set(u.fdc_id, g.name_bg);
        parent.set(find(g.id), "U");
    } else if (byId.has(d)) {
        const a = find(g.id), b = find(d);
        if (a !== b && a !== "U" && b !== "U") {
            if (rank(byId.get(a)) >= rank(byId.get(b))) parent.set(b, a); else parent.set(a, b);
        } else if (b === "U" || a === "U") {
            parent.set(a, "U"); parent.set(b, "U");
        }
    }
}
const keptGeneric = generic.filter((g) => find(g.id) === g.id);
// Some tables prefix names with emoji ("🧀 Кашкавал краве").
const clean = (name) => name.replace(/[\p{Extended_Pictographic}\u200d\ufe0f]/gu, "").replace(/\s+/g, " ").trim();
const seenNames = new Set();
const out = [];
for (const g of keptGeneric) {
    const key = clean(g.name_bg).toLowerCase();
    if (seenNames.has(key)) continue;
    seenNames.add(key);
    out.push({ kind: "generic", barcode: "", name_bg: clean(g.name_bg), name_en: english[g.name_bg] || "", brand: "", kcal: g.kcal, protein: g.protein, carbs: g.carbs, fat: g.fat, serving_g: "", source: g.source, fiber: "", satfat: "", piece_g: "", basis: "" });
}
for (const p of off) {
    out.push({ kind: "product", barcode: p.barcode, name_bg: clean(p.name_bg), name_en: "", brand: p.brand, kcal: p.kcal, protein: p.protein, carbs: p.carbs, fat: p.fat, serving_g: p.serving_g, source: "https://world.openfoodfacts.org/product/" + p.barcode, fiber: p.fiber ?? "", satfat: p.satfat ?? "", piece_g: "", basis: "" });
}
// Hand-curated foods with their own sources, kept as they are.
for (const curated of ["lidl-bakery.csv"]) {
    out.push(...readCsv(path.join(here, curated)));
}

// A Latin letter that looks Cyrillic inside a Cyrillic word ("чипc"), or the reverse, takes the alphabet of the rest
// of its word; the same rule as Plate.Core MixedScript, which the app also applies when seeding.
const LATIN = "aeopcxyAEOPCXBKMHT", CYRILLIC = "аеорсхуАЕОРСХВКМНТ";
const mixedScript = (text) => (text || "").replace(/\p{L}+/gu, (w) => {
    const c = [...w].filter((ch) => /[\u0400-\u04FF]/.test(ch)).length;
    const l = [...w].filter((ch) => /[A-Za-z]/.test(ch)).length;
    if (!c || !l) return w;
    const [from, to] = c >= l ? [LATIN, CYRILLIC] : [CYRILLIC, LATIN];
    return [...w].map((ch) => (from.includes(ch) ? to[from.indexOf(ch)] : ch)).join("");
});
for (const row of out) {
    row.name_bg = mixedScript(row.name_bg);
    row.name_en = mixedScript(row.name_en);
    row.brand = mixedScript(row.brand);
}

writeCsv(path.join(seedDir, "bg-foods.csv"), ["kind", "barcode", "name_bg", "name_en", "brand", "kcal", "protein", "carbs", "fat", "serving_g", "source", "fiber", "satfat", "piece_g", "basis"], out);
writeCsv(path.join(raw, "usda-bg-names.csv"), ["fdc_id", "name_bg"], [...usdaNames].map(([fdc_id, name_bg]) => ({ fdc_id, name_bg })));
console.log(`kept ${keptGeneric.length} generic (${generic.length - keptGeneric.length} duplicates removed, ${usdaNames.size} of them matched USDA), ${off.length} products`);
