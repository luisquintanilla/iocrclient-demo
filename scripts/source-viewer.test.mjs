import { test } from "node:test";
import assert from "node:assert/strict";
import { projectRegions } from "../hero/hero.Web/wwwroot/source-viewer.js";

const frame = { width: 612, height: 792, unit: "Point", origin: "BottomLeft" };
const region = { kind: "text", polygon: [[72, 72], [144, 72], [144, 144]], confidenceKind: "layout", confidence: 0.85 };
const project = (sourceFrame = frame, regions = [region], view = [0, 0, 612, 792], rotation = 0) =>
  projectRegions(sourceFrame, regions, view, 918, 1188, rotation);

test("bottom-left source points map within one billionth of a rendered pixel", () => {
  const result = project();
  const expected = [[108, 1080], [216, 1080], [216, 972]];
  assert.equal(result.polygons.length, 1);
  for (let point = 0; point < expected.length; point++) {
    for (let axis = 0; axis < 2; axis++) {
      assert.ok(Math.abs(result.polygons[0].points[point][axis] - expected[point][axis]) <= 1e-9);
    }
  }
});

test("top-left source points do not receive a second vertical flip", () => {
  assert.deepEqual(project({ ...frame, origin: "TopLeft" }).polygons[0].points, [[108, 108], [216, 108], [216, 216]]);
});

test("projection preserves detection confidence meaning instead of relabelling it OCR confidence", () => {
  const result = project().polygons[0];
  assert.equal(result.kind, "text");
  assert.equal(result.confidenceKind, "layout");
  assert.equal(result.confidence, 0.85);
});

test("unknown coordinate units block projection rather than assuming pixels", () => {
  const result = project({ ...frame, unit: null });
  assert.match(result.status, /^Overlay unavailable:/);
  assert.deepEqual(result.polygons, []);
});

test("missing coordinate dimensions block projection", () => {
  const result = project({ ...frame, width: null });
  assert.match(result.status, /^Overlay unavailable:/);
  assert.deepEqual(result.polygons, []);
});

test("different page aspect ratios block projection instead of stretching geometry", () => {
  const result = project({ ...frame, width: 792, height: 612 });
  assert.match(result.status, /different aspect ratios/);
  assert.deepEqual(result.polygons, []);
});

test("rotated and offset PDF pages require an explicit verified transform", () => {
  for (const [view, rotation] of [[[0, 0, 612, 792], 90], [[10, 20, 622, 812], 0]]) {
    const result = project(frame, [region], view, rotation);
    assert.match(result.status, /explicit verified frame transform/);
    assert.deepEqual(result.polygons, []);
  }
});

test("non-finite persisted polygon coordinates fail explicitly", () => {
  assert.throws(() => project(frame, [{ ...region, polygon: [[NaN, 1], [2, 3], [4, 5]] }]), /non-finite/);
});
