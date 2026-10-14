import { writeFile } from "node:fs/promises";
import assert from "node:assert/strict";

const [debuggerOrigin, sourceUrl, outputPrefix] = process.argv.slice(2);
if (!debuggerOrigin || !sourceUrl || !outputPrefix) {
  throw new Error("Usage: node verify-source-browser.mjs <isolated CDP origin> <source URL> <output prefix>");
}
const response = await fetch(`${debuggerOrigin}/json/new`, { method: "PUT" });
assert.equal(response.ok, true, "The isolated browser did not create a source tab.");
const target = await response.json();
const socket = new WebSocket(target.webSocketDebuggerUrl);
const pending = new Map();
let nextId = 0;
socket.addEventListener("message", event => {
  const message = JSON.parse(event.data);
  if (!message.id) return;
  const request = pending.get(message.id);
  if (!request) return;
  pending.delete(message.id);
  clearTimeout(request.timeout);
  if (message.error) request.reject(new Error(JSON.stringify(message.error)));
  else request.resolve(message.result);
});
await new Promise((resolve, reject) => {
  socket.addEventListener("open", resolve, { once: true });
  socket.addEventListener("error", reject, { once: true });
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Browser command timed out: ${method}`));
    }, 30_000);
    pending.set(id, { resolve, reject, timeout });
    socket.send(JSON.stringify({ id, method, params }));
  });
}
try {
  await command("Page.enable");
  await command("Runtime.enable");
  const navigation = await command("Page.navigate", { url: sourceUrl });
  assert.equal(navigation.errorText, undefined, "The browser could not navigate to the running hero.");
  const result = await command("Runtime.evaluate", {
    awaitPromise: true,
    returnByValue: true,
    expression: `(async () => {
      for (let i = 0; i < 80; i++) {
        if (document.querySelector("canvas") && document.querySelector("svg polygon")) break;
        await new Promise(resolve => setTimeout(resolve, 250));
      }
      document.querySelector("details summary")?.click();
      return {
        title: document.title,
        text: document.body.innerText,
        canvases: [...document.querySelectorAll("canvas")].map(canvas => ({width: canvas.width, height: canvas.height})),
        polygons: [...document.querySelectorAll("svg polygon")].map(polygon => polygon.getAttribute("points")),
        pages: [...document.querySelectorAll("select option")].map(option => option.textContent),
        sourceUrl: [...document.querySelectorAll("a")].find(link => link.textContent === "Open verified source")?.href
      };
    })()`,
  });
  assert.equal(result.exceptionDetails, undefined, "Browser evaluation failed.");
  const evidence = result.result.value;
  await writeFile(`${outputPrefix}.json`, JSON.stringify(evidence, null, 2));
  assert.match(evidence.text, /Source SHA-256:/, "The persisted source hash was not verified.");
  assert.match(evidence.text, /ORBIT-742/, "The persisted supporting text did not render.");
  assert.ok(evidence.canvases.some(canvas => canvas.width > 0 && canvas.height > 0), "The actual PDF page was not rendered.");
  assert.ok(evidence.polygons.length > 0, "Known native geometry was not projected onto the actual page.");
  const screenshot = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
  await writeFile(`${outputPrefix}.png`, Buffer.from(screenshot.data, "base64"));
  console.log(JSON.stringify({ status: "passed", sourceUrl, pages: evidence.pages, polygons: evidence.polygons.length }));
} finally {
  socket.close();
}
