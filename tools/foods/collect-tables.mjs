// Parses the two public Bulgarian calorie tables scraped with firecrawl into raw/tables.csv.
//   firecrawl scrape "https://www.spidersport.com/calories.php" --only-main-content -o tools/foods/raw/spidersport.md
//   firecrawl scrape "https://macroscope.top/" --only-main-content -o tools/foods/raw/macroscope.md
import fs from "node:fs";
import path from "node:path";
import { raw, writeCsv, plausible } from "./lib.mjs";

const rows = [];
const num = (s) => parseFloat(String(s).replace(",", ".").trim());
const clean = (s) => s.replace(/!\[[^\]]*\]\([^)]*\)/g, "").replace(/\[([^\]]*)\]\([^)]*\)/g, "$1").replace(/\*\*/g, "").replace(/\\/g, "").trim();

// Spider Sport: Продукт | kcal/100g | Мазн | Белт | Въгл
for (const line of fs.readFileSync(path.join(raw, "spidersport.md"), "utf8").split("\n")) {
    const cells = line.split("|").map((c) => c.trim()).filter((c, i, a) => i > 0 && i < a.length - 1);
    if (cells.length === 5 && /^[0-9.,]+$/.test(cells[1]) && !/kcal/i.test(cells[1])) {
        rows.push({ name_bg: clean(cells[0]), kcal: num(cells[1]), fat: num(cells[2]), protein: num(cells[3]), carbs: num(cells[4]), source: "https://www.spidersport.com/calories.php" });
    }
}

// MacroScope: Храна | Протеини | Мазнини | Въглехидрати | Калории | ➕
for (const line of fs.readFileSync(path.join(raw, "macroscope.md"), "utf8").split("\n")) {
    const cells = line.split("|").map((c) => c.trim()).filter((c, i, a) => i > 0 && i < a.length - 1);
    if (cells.length >= 5 && /^[0-9.,]+$/.test(cells[1]) && /^[0-9.,]+$/.test(cells[4])) {
        rows.push({ name_bg: clean(cells[0]), protein: num(cells[1]), fat: num(cells[2]), carbs: num(cells[3]), kcal: num(cells[4]), source: "https://macroscope.top/" });
    }
}

const kept = rows.filter((r) => r.name_bg && plausible(r));
writeCsv(path.join(raw, "tables.csv"), ["name_bg", "kcal", "protein", "carbs", "fat", "source"], kept);
console.log(`tables: ${rows.length} rows, ${kept.length} plausible`);
