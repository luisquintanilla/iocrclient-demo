export function projectRegions(frame, regions, view, width, height, rotation) {
  if (rotation % 360 !== 0 || view[0] !== 0 || view[1] !== 0) {
    return { status: "Overlay blocked: a rotated or offset PDF requires an explicit verified frame transform.", polygons: [] };
  }
  if (!(frame.width > 0 && frame.height > 0) ||
      !["Pixel", "Point", "Inch", "Normalized"].includes(frame.unit) ||
      !["TopLeft", "BottomLeft"].includes(frame.origin)) {
    return { status: "Overlay unavailable: dimensions, coordinate unit or origin were not supplied.", polygons: [] };
  }
  const ratio = (frame.width / frame.height) / ((view[2] - view[0]) / (view[3] - view[1]));
  if (!Number.isFinite(ratio) || Math.abs(ratio - 1) > 0.001) {
    return { status: "Overlay blocked: source and rendered page frames have different aspect ratios.", polygons: [] };
  }
  const polygons = regions.map(region => ({
    kind: region.kind,
    confidenceKind: region.confidenceKind,
    confidence: region.confidence,
    points: region.polygon.map(([x, y]) => {
      if (!Number.isFinite(x) || !Number.isFinite(y)) {
        throw new Error("A persisted source polygon contains non-finite coordinates.");
      }
      return [x / frame.width * width,
        (frame.origin === "BottomLeft" ? 1 - y / frame.height : y / frame.height) * height];
    }),
  }));
  return { status: `${polygons.length} source regions projected. Projection is not a claim of detection accuracy.`, polygons };
}

export async function render(container, url, frame, regions) {
  const pdfjs = await import("./lib/pdfjs-dist/dist/build/pdf.min.mjs");
  pdfjs.GlobalWorkerOptions.workerSrc = new URL("./lib/pdfjs-dist/dist/build/pdf.worker.min.mjs", import.meta.url).href;
  const document = await pdfjs.getDocument(url).promise;
  try {
    const page = await document.getPage(frame.pageNumber);
    const viewport = page.getViewport({ scale: 1.5 });
    const canvas = globalThis.document.createElement("canvas");
    canvas.width = Math.ceil(viewport.width);
    canvas.height = Math.ceil(viewport.height);
    canvas.style.width = "100%";
    canvas.style.display = "block";
    const context = canvas.getContext("2d");
    if (!context) {
      throw new Error("The browser could not create a page rendering context.");
    }
    await page.render({ canvasContext: context, viewport }).promise;
    const projected = projectRegions(frame, regions, page.view, viewport.width, viewport.height, page.rotate);
    const overlay = globalThis.document.createElementNS("http://www.w3.org/2000/svg", "svg");
    overlay.setAttribute("viewBox", `0 0 ${canvas.width} ${canvas.height}`);
    overlay.style.cssText = "position:absolute;inset:0;width:100%;height:100%;pointer-events:none";
    for (const region of projected.polygons) {
      const polygon = globalThis.document.createElementNS(overlay.namespaceURI, "polygon");
      polygon.setAttribute("points", region.points.map(point => point.join(",")).join(" "));
      polygon.setAttribute("fill", "rgba(0,120,255,0.12)");
      polygon.setAttribute("stroke", "#0078ff");
      polygon.setAttribute("stroke-width", "1.5");
      overlay.append(polygon);
    }
    container.replaceChildren(canvas, overlay);
    return projected.status;
  } finally {
    await document.destroy();
  }
}
