// Products sold in Bulgaria from Open Food Facts (ODbL, attribution in the README) through the search-a-licious
// API, one page a second. Keeps products with a Bulgarian name, a valid barcode and complete per-100 g values.
// Writes raw/off-bg.csv. The app still looks up any other barcode live.
import path from "node:path";
import { raw, writeCsv, plausible } from "./lib.mjs";

const UA = "GymTracker/1.0 (personal food-database import)";
const base = "https://search.openfoodfacts.org/search";
const q = 'countries_tags:"en:bulgaria" AND states_tags:"en:nutrition-facts-completed"';
const fields = "code,product_name,product_name_bg,generic_name_bg,brands,nutriments,serving_quantity,lang";
const cyrillic = /[а-яА-Я]/;

function validEan(code) {
    if (!/^\d{8}$|^\d{12,14}$/.test(code)) return false;
    const digits = code.split("").map(Number);
    const check = digits.pop();
    const sum = digits.reverse().reduce((s, d, i) => s + d * (i % 2 === 0 ? 3 : 1), 0);
    return (10 - (sum % 10)) % 10 === check;
}

const rows = new Map();
let page = 1, pages = 1;
while (page <= pages && page <= 100) {
    const url = `${base}?q=${encodeURIComponent(q)}&page=${page}&page_size=100&fields=${fields}`;
    const res = await fetch(url, { headers: { "User-Agent": UA } });
    if (!res.ok) {
        console.log(`page ${page}: ${res.status}, retrying`);
        await new Promise((r) => setTimeout(r, 5000));
        continue;
    }
    const json = await res.json();
    pages = json.page_count;
    for (const p of json.hits) {
        const n = p.nutriments || {};
        const name = [p.product_name_bg, p.product_name, p.generic_name_bg].find((x) => x && cyrillic.test(x));
        const row = {
            barcode: p.code,
            name_bg: name ? name.trim().replace(/\s+/g, " ") : "",
            brand: (p.brands || [])[0] || "",
            kcal: n["energy-kcal_100g"] ?? (n["energy_100g"] ? +(n["energy_100g"] / 4.184).toFixed(0) : undefined),
            protein: n.proteins_100g, carbs: n.carbohydrates_100g, fat: n.fat_100g,
            fiber: n.fiber_100g ?? "", satfat: n["saturated-fat_100g"] ?? "",
            serving_g: p.serving_quantity && +p.serving_quantity > 0 && +p.serving_quantity <= 2000 ? +p.serving_quantity : "",
        };
        if (row.name_bg && row.name_bg.length <= 200 && validEan(row.barcode) && plausible(row)) {
            rows.set(row.barcode, row);
        }
    }
    process.stdout.write(`page ${page}/${pages}: ${rows.size} kept\r`);
    page++;
    await new Promise((r) => setTimeout(r, 1000));
}
writeCsv(path.join(raw, "off-bg.csv"), ["barcode", "name_bg", "brand", "kcal", "protein", "carbs", "fat", "serving_g", "fiber", "satfat"], [...rows.values()]);
console.log(`\noff: ${rows.size} products`);
