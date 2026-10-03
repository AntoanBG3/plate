// Shared helpers for the food data pipeline (dev only).
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const here = path.dirname(new URL(import.meta.url).pathname);
export const raw = path.join(here, "raw");
export const repo = path.resolve(here, "../..");
fs.mkdirSync(raw, { recursive: true });

export function geminiKey() {
    if (process.env.GEMINI_API_KEY) {
        return process.env.GEMINI_API_KEY.trim();
    }
    return fs.readFileSync(path.join(os.homedir(), ".config/gymtracker/gemini-key"), "utf8").trim();
}

/** Calls Gemini with structured JSON output and returns the parsed result. */
export async function gemini({ model = "gemini-3.8-flash", system, prompt, schema, temperature = 0.1 }) {
    for (let attempt = 1; attempt <= 4; attempt++) {
        let res;
        try {
            res = await fetch(`https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent`, {
            method: "POST",
            signal: AbortSignal.timeout(90000),
            headers: { "content-type": "application/json", "x-goog-api-key": geminiKey() },
            body: JSON.stringify({
                systemInstruction: system ? { parts: [{ text: system }] } : undefined,
                contents: [{ role: "user", parts: [{ text: prompt }] }],
                generationConfig: { temperature, responseMimeType: "application/json", responseJsonSchema: schema },
            }),
        });
        } catch {
            continue; // timed out or the connection dropped: try again
        }
        if (res.status === 429 || res.status >= 500) {
            await new Promise((r) => setTimeout(r, 2000 * attempt));
            continue;
        }
        if (!res.ok) {
            throw new Error(`Gemini ${res.status}: ${(await res.text()).slice(0, 300)}`);
        }
        const json = await res.json();
        return JSON.parse(json.candidates?.[0]?.content?.parts?.map((p) => p.text || "").join("") || "null");
    }
    throw new Error("Gemini kept failing");
}

export function csvField(v) {
    const s = v === null || v === undefined ? "" : String(v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function writeCsv(file, header, rows) {
    fs.writeFileSync(file, [header.join(","), ...rows.map((r) => header.map((h) => csvField(r[h])).join(","))].join("\n") + "\n");
}

export function readCsv(file) {
    const lines = fs.readFileSync(file, "utf8").split("\n").filter(Boolean);
    const parse = (line) => {
        const out = [];
        let field = "";
        let quoted = false;
        for (let i = 0; i < line.length; i++) {
            const c = line[i];
            if (quoted) {
                if (c === '"' && line[i + 1] === '"') { field += '"'; i++; }
                else if (c === '"') { quoted = false; }
                else { field += c; }
            } else if (c === '"') { quoted = true; }
            else if (c === ",") { out.push(field); field = ""; }
            else { field += c; }
        }
        out.push(field);
        return out;
    };
    const header = parse(lines[0]);
    return lines.slice(1).map((l) => Object.fromEntries(parse(l).map((v, i) => [header[i], v])));
}

/** A row is plausible when macros fit in 100 g and roughly explain the energy. */
export function plausible(r) {
    const k = +r.kcal, p = +r.protein, c = +r.carbs, f = +r.fat;
    if (![k, p, c, f].every((x) => Number.isFinite(x) && x >= 0)) return false;
    if (k > 900 || p + c + f > 100.5) return false;
    const fromMacros = 4 * p + 4 * c + 9 * f;
    // Alcohol and fibre make this loose; only reject wild mismatches.
    return k === 0 ? fromMacros < 5 : Math.abs(k - fromMacros) <= Math.max(40, 0.35 * k);
}

/** Runs fn over items with at most `limit` in flight; results keep the input order. */
export async function pool(items, limit, fn) {
    const out = new Array(items.length);
    let next = 0;
    await Promise.all(Array.from({ length: limit }, async () => {
        while (next < items.length) {
            const i = next++;
            out[i] = await fn(items[i], i);
        }
    }));
    return out;
}
