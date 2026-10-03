// Inlines the shared motion CSS, icons and behaviour so each sample is one self-contained file.
//   node design-samples/tools/build.mjs
import fs from "node:fs";
import path from "node:path";
import { session } from "./session-markup.mjs";

const dir = path.join(path.dirname(new URL(import.meta.url).pathname), "..");
const read = (f) => fs.readFileSync(path.join(dir, "src", f), "utf8");
const parts = { motion: read("motion.css"), icons: read("icons.html"), shared: read("shared.js"), session: session() };
for (const file of fs.readdirSync(path.join(dir, "src")).filter((f) => /^[a-z]-.*\.html$/.test(f))) {
    const html = read(file)
        .replace("/* @motion */", () => parts.motion)
        .replace("<!-- @icons -->", () => parts.icons)
        .replace("/* @shared */", () => parts.shared)
        .replace("<!-- @session -->", () => parts.session);
    fs.writeFileSync(path.join(dir, file), html);
    console.log("built", file);
}
