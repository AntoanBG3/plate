// Fills the fiber and satfat columns of the seed's bg-foods.csv for its Open Food Facts products, looked up by
// barcode through the search-a-licious API, 100 at a time, one request a second. Dishes have no source for these,
// so they stay blank (unknown). Values that are negative, fiber over 100 g or saturated fat above fat are dropped.
//   node tools/foods/add-fiber.mjs
import path from "node:path";
import { repo, readCsv, writeCsv } from "./lib.mjs";

const UA = "Plate/1.0 (personal food-database import)";
const base = "https://search.openfoodfacts.org/search";
const file = path.join(repo, "src/Plate.Web/Data/Seed/bg-foods.csv");
const header = ["kind", "barcode", "name_bg", "name_en", "brand", "kcal", "protein", "carbs", "fat", "serving_g", "source", "fiber", "satfat"];

const rows = readCsv(file);
const products = rows.filter((r) => r.kind === "product" && r.barcode);
const found = new Map();
for (let i = 0; i < products.length; i += 100) {
    const batch = products.slice(i, i + 100);
    const q = `code:(${batch.map((r) => r.barcode).join(" OR ")})`;
    const url = `${base}?q=${encodeURIComponent(q)}&page_size=100&fields=code,nutriments`;
    const res = await fetch(url, { headers: { "User-Agent": UA } });
    if (!res.ok) {
        console.log(`batch ${i / 100 + 1}: ${res.status}, retrying`);
        await new Promise((r) => setTimeout(r, 5000));
        i -= 100;
        continue;
    }
    for (const p of (await res.json()).hits) {
        found.set(p.code, p.nutriments || {});
    }
    process.stdout.write(`${Math.min(i + 100, products.length)}/${products.length} looked up\r`);
    await new Promise((r) => setTimeout(r, 1000));
}

const num = (v) => (v === undefined || v === null || v === "" || isNaN(+v) ? undefined : +v);
let fiber = 0, satfat = 0;
for (const r of rows) {
    const n = r.kind === "product" ? found.get(r.barcode) : undefined;
    const f = num(n?.fiber_100g);
    const s = num(n?.["saturated-fat_100g"]);
    r.fiber = f !== undefined && f >= 0 && f <= 100 ? f : r.fiber ?? "";
    r.satfat = s !== undefined && s >= 0 && s <= +r.fat + 0.5 ? s : r.satfat ?? "";
    if (r.fiber !== "") fiber++;
    if (r.satfat !== "") satfat++;
}
writeCsv(file, header, rows);
console.log(`\n${found.size} of ${products.length} products found; fiber on ${fiber}, saturated fat on ${satfat} of ${rows.length} rows`);
