// Shared helpers for the food data pipeline (dev only).
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const here = path.dirname(new URL(import.meta.url).pathname);
export const raw = path.join(here, "raw");
export const repo = path.resolve(here, "../..");
fs.mkdirSync(raw, { recursive: true });

export function openRouterKey() {
    if (process.env.OPENROUTER_API_KEY) {
        return process.env.OPENROUTER_API_KEY.trim();
    }
    const file = path.join(os.homedir(), ".config/gymtracker/openrouter-key");
    if (!fs.existsSync(file)) {
        throw new Error(`No OpenRouter key: set OPENROUTER_API_KEY or save it to ${file}`);
    }
    return fs.readFileSync(file, "utf8").trim();
}

export const defaultModel = process.env.OPENROUTER_MODEL || "google/gemini-3.8-flash";

let catalog;
/** The model's catalog entry from OpenRouter (public endpoint), or undefined. Cached per process. */
async function modelInfo(model) {
    catalog ??= fetch("https://openrouter.ai/api/v1/models", { signal: AbortSignal.timeout(30000) })
        .then((r) => r.json()).then((j) => j.data).catch(() => []);
    return (await catalog).find((m) => m.id === model);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Calls an OpenRouter model with strict structured JSON output and returns the parsed result. Strict schemas need an
 * object at the root, so an array schema is wrapped as { items: [...] } and unwrapped again.
 */
export async function llm({ model = defaultModel, system, prompt, schema, temperature = 0.1 }) {
    const key = openRouterKey();
    const wrapped = schema.type === "array";
    const rootSchema = wrapped ? { type: "object", properties: { items: schema }, required: ["items"], additionalProperties: false } : schema;
    const info = await modelInfo(model);
    const supports = new Set(info?.supported_parameters || []);
    const body = {
        model,
        messages: [...(system ? [{ role: "system", content: system }] : []), { role: "user", content: prompt }],
        response_format: { type: "json_schema", json_schema: { name: "result", strict: true, schema: strict(rootSchema) } },
        provider: { require_parameters: true },
    };
    if (supports.has("temperature")) body.temperature = temperature;
    const efforts = info?.reasoning?.supported_efforts;
    if (supports.has("reasoning") && efforts?.length) body.reasoning = { effort: efforts.at(-1) };

    let lastError = "";
    for (let attempt = 1; attempt <= 5; attempt++) {
        let res;
        try {
            res = await fetch("https://openrouter.ai/api/v1/chat/completions", {
                method: "POST",
                signal: AbortSignal.timeout(120000),
                headers: { "content-type": "application/json", authorization: `Bearer ${key}`, "x-title": "GymTracker tools" },
                body: JSON.stringify(body),
            });
        } catch (e) {
            lastError = e.message; // timed out or the connection dropped
            await sleep(2000 * 2 ** attempt);
            continue;
        }
        if (res.status === 429 || res.status >= 500) {
            lastError = `${res.status} ${(await res.text()).slice(0, 200)}`;
            await sleep(2000 * 2 ** attempt);
            continue;
        }
        if (!res.ok) {
            throw new Error(`OpenRouter ${res.status}: ${(await res.text()).slice(0, 300)}`);
        }
        const json = await res.json();
        const text = json.choices?.[0]?.message?.content;
        if (!text) {
            lastError = `empty answer: ${JSON.stringify(json).slice(0, 200)}`;
            continue;
        }
        const parsed = JSON.parse(text);
        return wrapped ? parsed.items : parsed;
    }
    throw new Error(`OpenRouter kept failing (${model}): ${lastError}`);
}

/** Makes a JSON schema strict-mode compatible: no extra properties, every property required. */
function strict(schema) {
    if (schema?.type === "object" || schema?.properties) {
        const properties = Object.fromEntries(Object.entries(schema.properties || {}).map(([k, v]) => [k, strict(v)]));
        return { ...schema, properties, required: Object.keys(properties), additionalProperties: false };
    }
    if (schema?.items) {
        return { ...schema, items: strict(schema.items) };
    }
    return schema;
}

export function csvField(v) {
    const s = v === null || v === undefined ? "" : String(v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function writeCsv(file, header, rows) {
    fs.writeFileSync(file, [header.join(","), ...rows.map((r) => header.map((h) => csvField(r[h])).join(","))].join("\n") + "\n");
}

export function readCsv(file) {
    const lines = fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean);
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
