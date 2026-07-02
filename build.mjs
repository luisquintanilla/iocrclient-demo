// Build script: assembles the static site into public/ for GitHub Pages.
// It copies the deck, styles, themes, assets, and the reveal.js files we load.
// It is theme- and file-count-agnostic: adding a theme or a CSS file needs no
// change here. Cache-busting is applied to every local CSS link and the deck.

import {
  cpSync,
  rmSync,
  mkdirSync,
  existsSync,
  readFileSync,
  writeFileSync,
  readdirSync,
} from "node:fs";
import { createHash } from "node:crypto";
import { join } from "node:path";

const out = "public";

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const items = ["index.html", "slides.md", "css", "themes", "assets"];
for (const item of items) {
  if (existsSync(item)) {
    cpSync(item, join(out, item), { recursive: true });
  }
}

function readDeckTitle(markdown) {
  const match = markdown.match(/^<!--\s*([\s\S]*?)\s*-->/);
  if (!match) return "TODO Talk Title";

  for (const line of match[1].split(/\r?\n/)) {
    const title = line.match(/^\s*title:\s*(.+?)\s*$/);
    if (title) return title[1];
  }

  return "TODO Talk Title";
}

// Hash every style and the deck so a change to any of them busts the cache.
function listCss(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((f) => f.endsWith(".css"))
    .map((f) => join(dir, f));
}

const styleFiles = [...listCss("css"), ...listCss("themes"), "slides.md"].filter(existsSync);
const ver = styleFiles
  .map((f) => readFileSync(f))
  .reduce((h, buf) => h.update(buf), createHash("sha256"))
  .digest("hex")
  .slice(0, 10);

const indexPath = join(out, "index.html");
const slides = readFileSync("slides.md", "utf8");
let html = readFileSync(indexPath, "utf8");
html = html
  .replace("TODO Talk Title", readDeckTitle(slides))
  // Append a cache-busting version to every local css/ and themes/ stylesheet.
  .replace(/href="((?:css|themes)\/[^"]+\.css)"/g, `href="$1?v=${ver}"`)
  .replace('data-markdown="slides.md"', `data-markdown="slides.md?v=${ver}"`);
writeFileSync(indexPath, html);

const reveal = "node_modules/reveal.js";
if (!existsSync(reveal)) {
  console.error("reveal.js not found. Run `npm install` first.");
  process.exit(1);
}
cpSync(join(reveal, "dist"), join(out, "reveal", "dist"), { recursive: true });
cpSync(join(reveal, "plugin"), join(out, "reveal", "plugin"), { recursive: true });

console.log("Built site into ./public");
