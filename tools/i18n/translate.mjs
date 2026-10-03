// Translates the interface strings into Bulgarian with an LLM through OpenRouter (dev-only; the app never calls this).
//
//   GYM_WRITE_KEYS=1 dotnet test --filter CatalogueTests      # writes src/GymTracker.Web/Resources/strings.keys.json
//   node tools/i18n/translate.mjs [--all] [--dry-run]           # fills in strings.bg.json
//
// Only keys missing from strings.bg.json are sent unless --all is given. The API key is read from OPENROUTER_API_KEY
// or ~/.config/gymtracker/openrouter-key; the model from OPENROUTER_MODEL (see the default below), the reasoning effort
// from OPENROUTER_EFFORT.
// Review the output by hand before committing: this produces a draft, not the final text.
import fs from "node:fs";
import path from "node:path";
import { llm } from "../foods/lib.mjs";

const repo = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../..");
const keysFile = path.join(repo, "src/GymTracker.Web/Resources/strings.keys.json");
const bgFile = path.join(repo, "src/GymTracker.Web/Resources/strings.bg.json");
const all = process.argv.includes("--all");
const dryRun = process.argv.includes("--dry-run");

const keys = JSON.parse(fs.readFileSync(keysFile, "utf8"));
const bg = fs.existsSync(bgFile) ? JSON.parse(fs.readFileSync(bgFile, "utf8")) : {};
const todo = Object.keys(keys).filter((k) => all || !(k in bg));
console.log(`${Object.keys(keys).length} strings, ${todo.length} to translate`);
if (todo.length === 0) {
    process.exit(0);
}

// Labels that sit in the tab bar, segmented controls or small buttons.
const SHORT = new Set(["Today", "Train", "Food", "Progress", "Settings", "Type", "Scan", "Search", "Recent", "Manual", "4 weeks", "12 weeks", "All",
    "Dark", "Light", "Match phone", "Skip", "Finish", "Done", "Add", "Start", "Edit", "Save", "Update", "Cancel", "Rename", "Back", "W", "Rest", "Warm-up", "Add set"]);

function hint(key) {
    const where = keys[key].join(", ");
    const notes = [];
    if (SHORT.has(key)) {
        notes.push("very short label (tab bar, segmented control or small button): at most 10 characters if at all possible");
    }
    if (where.includes("exercise")) {
        notes.push("name of a gym exercise as Bulgarian lifters say it in the gym");
    }
    if (where.includes("enum")) {
        notes.push("a muscle group, meal, equipment type or goal, used as a heading or picker value");
    }
    if (where.includes("wwwroot/js")) {
        notes.push("shown by JavaScript (toast, timer, scanner or chart label)");
    }
    return { en: key, where, notes: notes.join("; ") };
}

const BRIEF = `
You localize the interface of a personal gym and nutrition tracker into Bulgarian. It is used as an iPhone app
by two Bulgarian men (a son in his twenties and his father), at the gym between sets and in the kitchen.
This is localization, not translation: write what a native Bulgarian product writer would put in a carefully
localized iOS app, not a word-for-word rendering of the English.

Voice and form
- Address the user informally with "ти" (as Bulgarian versions of Apple, Revolut and fitness apps do). Never "Вие".
- Plain, calm, friendly. No exclamation marks unless the English has one. No marketing tone.
- Sentence case. Bulgarian does not capitalise every word of a title.
- Buttons are short imperatives (Запази, Добави, Започни, Пропусни). Tabs and segment labels are single short nouns.
- Keep sentences short; split or restructure when Bulgarian word order wants it.
- Use natural Bulgarian quotation marks „…“ if quotes are needed. Use the ellipsis character … where the English does.

Domain vocabulary (use these consistently)
- set = серия (serii), rep = повторение (повторения), warm-up set = загрявка / загряваща серия, rest = почивка,
  workout = тренировка, template = шаблон, exercise = упражнение, weight (on the bar) = тежест,
  bodyweight = телесно тегло, trend weight = тренд на теглото, weigh-in = измерване (на теглото),
  estimated 1RM = прогнозен максимум (1ПМ) — keep "1ПМ" as the short form,
  maintenance (calories) = поддържащи калории, target = цел, cut = отслабване (сваляне), bulk = качване (на маса),
  maintain = поддържане, protein = протеин, carbs = въглехидрати, fat = мазнини, kcal = ккал,
  meal names: Breakfast = Закуска, Lunch = Обяд, Dinner = Вечеря, Snack = Междинно хранене (as a heading) / "нещо между храненията" in prose,
  food diary = дневник на храненето, barcode = баркод, label (on packaging) = етикет.
- Exercises: use the names Bulgarian gym-goers use: Bench press = Лежанка, Incline bench press = Лежанка на наклон,
  Squat = Клек, Front squat = Преден клек, Deadlift = Мъртва тяга, Romanian deadlift = Румънска тяга,
  Pull-up = Набирания, Chin-up = Набирания с подхват, Dip = Кофички, Push-up = Лицеви опори,
  Barbell row = Гребане с щанга, Overhead press = Раменна преса с щанга, Lat pulldown = Скрипец към гърди,
  Lateral raise = Разтваряне встрани, Leg press = Лег преса, Hip thrust = Хип тръст, Plank = Планк,
  Skull crusher = Френско разгъване, Face pull = Фейс пул. Follow that register for the rest.
- Muscles: Chest = Гърди, Back = Гръб, Shoulders = Рамене, Biceps = Бицепс, Triceps = Трицепс, Forearms = Предмишници,
  Quads = Квадрицепс, Hamstrings = Задно бедро, Glutes = Седалище, Calves = Прасци, Abs = Корем.
- Units: kg = кг, g = г, cm = см, s (seconds) = с, h = ч, min = мин. Keep numbers and their format exactly as in the English.

Hard rules
- Keep every placeholder ({0}, {1}, …) exactly once, unchanged; move it wherever Bulgarian grammar wants it.
- Keep product and format names as they are: OpenRouter, Open Food Facts, USDA, CSV, EAN, UPC, .env, GymTracker.
- Strings that already contain Bulgarian (example food names) keep that Bulgarian.
- "W" is the label for a warm-up set in a narrow column: use a single letter (З).
- If a string is a count form ("1 set" / "{0} sets"), give the matching Bulgarian form (1 серия / {0} серии).
- Return every input string, with "en" copied exactly.
`;

// A strong writer rather than a fast model: these strings are few and the user reads every one.
const model = process.env.OPENROUTER_MODEL || "google/gemini-4-carbon";

const translate = (batch) => llm({
    model,
    system: BRIEF,
    prompt: "Localize these interface strings. Each has where it appears in the app and notes.\n" + JSON.stringify(batch.map(hint), null, 1),
    schema: { type: "array", items: { type: "object", properties: { en: { type: "string" }, bg: { type: "string" } }, required: ["en", "bg"] } },
    temperature: 0.3,
});

if (dryRun) {
    console.log(BRIEF);
    console.log(JSON.stringify(todo.slice(0, 10).map(hint), null, 1));
    process.exit(0);
}

console.log("model:", model);

let pending = todo;
for (let round = 1; round <= 3 && pending.length; round++) {
    const failed = [];
    for (let i = 0; i < pending.length; i += 50) {
        const batch = pending.slice(i, i + 50);
        let out = [];
        try {
            out = (await translate(batch)) || [];
        } catch (e) {
            console.error(e.message);
        }
        const byEn = new Map(out.map((o) => [o.en, o.bg]));
        for (const en of batch) {
            const text = byEn.get(en);
            if (!text || placeholders(text) !== placeholders(en)) {
                failed.push(en);
                continue;
            }
            bg[en] = text.trim();
        }
        process.stdout.write(`.`);
    }
    console.log(`\nround ${round}: ${pending.length - failed.length} done, ${failed.length} to retry`);
    pending = failed;
}

const sorted = Object.fromEntries(Object.keys(bg).sort().map((k) => [k, bg[k]]));
fs.writeFileSync(bgFile, JSON.stringify(sorted, null, 2) + "\n");
if (pending.length) {
    console.log("Not translated:", pending);
}
