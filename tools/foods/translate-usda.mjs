// Adds Bulgarian names to the USDA subset (src/GymTracker.Web/Data/Seed/usda-foods.csv, column name_bg) with
// Gemini 3.8 Flash. Cached in raw/usda-bg.json, so re-runs only translate new rows. Run again after
// tools/usda/build_usda_subset.py rebuilds the CSV.
import fs from "node:fs";
import path from "node:path";
import { raw, repo, gemini, readCsv, writeCsv, pool } from "./lib.mjs";

const file = path.join(repo, "src/GymTracker.Web/Data/Seed/usda-foods.csv");
const rows = readCsv(file);
const cacheFile = path.join(raw, "usda-bg.json");
const cache = fs.existsSync(cacheFile) ? JSON.parse(fs.readFileSync(cacheFile, "utf8")) : {};
const todo = [...new Set(rows.map((r) => r.name))].filter((n) => !cache[n]);
console.log(`${rows.length} rows, ${todo.length} names to translate`);

const SYSTEM = `Translate USDA FoodData Central food names into the Bulgarian a person would search for or see in a
Bulgarian food diary or on a Bulgarian label. Natural, short, not word for word: lead with the food itself
("Банан, суров", "Пилешко филе, без кожа, печено", "Кисело мляко, пълномаслено", "Ориз бял, варен"), keep the
qualifiers that change nutrition (raw/cooked, how it was cooked, with or without skin, fat %, salted), and drop USDA
filler ("NFS", "commercially prepared" when it adds nothing, enrichment notes). Brand names stay as they are. Use
Bulgarian food words people actually use (кашкавал for yellow cheese, сирене for white brined cheese, кайма, филе,
бут, плешка, свинско, телешко, пилешко, пуешко, кренвирш, наденица, сок, нектар).`;

let done = 0;
const batches = [];
for (let i = 0; i < todo.length; i += 100) batches.push(todo.slice(i, i + 100));
await pool(batches, 4, async (batch) => {
    const out = await gemini({
        system: SYSTEM,
        prompt: JSON.stringify(batch),
        schema: { type: "array", items: { type: "object", properties: { en: { type: "string" }, bg: { type: "string" } }, required: ["en", "bg"] } },
        temperature: 0.2,
    }).catch(() => []);
    for (const o of out || []) {
        if (batch.includes(o.en) && o.bg && /[а-я]/i.test(o.bg)) cache[o.en] = o.bg.trim().slice(0, 200);
    }
    done += batch.length;
    fs.writeFileSync(cacheFile, JSON.stringify(cache));
    process.stdout.write(`${done}/${todo.length}\r`);
});

const missing = rows.filter((r) => !cache[r.name]).length;
writeCsv(file, ["fdc_id", "name", "kcal", "protein", "carbs", "fat", "name_bg"], rows.map((r) => ({ ...r, name_bg: cache[r.name] || "" })));
console.log(`\nwritten; ${missing} rows still without a Bulgarian name (run again to retry)`);
