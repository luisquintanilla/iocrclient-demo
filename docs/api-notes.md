# API notes — Round 2 proposals for `IOcrClient` (#7588)

These notes back two small, PR-ready additions to the `IOcrClient` abstraction in
`dotnet/extensions` (#7588) plus one naming refinement, and one implementation-side pattern for our
vision-LLM provider that is **not** a #7588 ask. Each was prototyped in the demo graft, validated
**across providers**, and only then proposed — prototype-first.

The abstraction changes are staged as clean commits on the `feature/ocr-abstractions` branch
(unpushed); the evidence below is meant to travel with the PR discussion.

---

## Proposal 1 — `OcrImage` + `OcrPage.Images`

### The gap
`OcrOptions.IncludeImages` already exists as a **request** flag, but `OcrPage` had no **sink** for
the images an engine returns (only `Tables` and `Blocks`). Requesting images had nowhere to land.

### The shape
```csharp
public sealed class OcrImage
{
    public DataContent? Content { get; set; }          // rendered bytes, when the engine returns them
    public OcrBoundingRegion? BoundingRegion { get; set; }
    public string? Caption { get; set; }               // description, when available
    public double? Confidence { get; set; }
}

// OcrPage:
public IReadOnlyList<OcrImage> Images { get; set; } = [];
```

Every member is **optional**, following the "optional nullable fields populated by implementers when
meaningful" guidance. `Content` is nullable on purpose (see the archetypes below).

### Cross-provider validation (live, `09-images-and-uris.cs` over a 2-page USGS fact sheet)
| Provider | Archetype | Result |
| --- | --- | --- |
| **Mistral OCR** | document-native | 1 image with **bytes + bbox** (`include_image_base64=true` → `page.images[]`) |
| **Azure Document Intelligence** (v4.0 GA) | document-native | 3 figures with **cropped bytes + bbox**, one carrying a **caption** (`output=figures` → `GET /analyzeResults/{id}/figures/{figureId}`) |
| **Content Understanding** | document-native | figures/media fit the same shape (contentUri/base64) |
| **Vision-LLM transcriber** | vision-LLM | **caption only** — a VLM cannot emit rendered image bytes (`Content` stays null) |

Concrete evidence from the run:
- Mistral p0: 1 image, 28 KB JPEG, bbox `(386, 519, 743, 915)` (pixel coords).
- Azure DI p0: 3 figures (24 KB / 324 KB / 20 KB PNG); figure 3 caption =
  *"Figure 1. Map showing the four continuous assessment units (AUs) in the Timan-Pechora Basin
  Province of Russia…"*; bbox in normalized inches.

**Why this clears the bar:** the shape generalizes across **≥2 real document-native engines** and
**degrades gracefully** for the vision-LLM archetype. Document-native engines fill `Content` (bytes);
a VLM fills only `Caption`. One shape, both archetypes — which is exactly why `Content` is nullable.

### Open questions for the PR
- Coordinate space: providers disagree (Mistral = pixels, DI = normalized inches). `BoundingRegion`
  carries raw provider geometry; a normalization helper could be a follow-up, not part of this change.
- Secondary gaps (selection marks, key-value pairs, per-image language) stay in
  `AdditionalProperties` until a second engine justifies promoting them.

---

## Proposal 2 — `UriContent` overload on `OcrClientExtensions`

### The gap
`#7588` ships a `DataContent` overload (sugar over the `Stream` core method). `UriContent : AIContent`
already exists in `dotnet/extensions`, and both Mistral (`document_url`) and Azure DI (`uriSource`)
accept a **native URL** input — but there was no `UriContent` entry point, so the overload set was
asymmetric.

### The shape
```csharp
public static Task<OcrResult> ExtractAsync(
    this IOcrClient client, UriContent document, OcrOptions? options = null,
    CancellationToken cancellationToken = default);
```

- Handles self-contained **`data:` URIs** by delegating to the `DataContent` overload.
- Does **no file or network IO**. For `file:` and remote (`http`/`https`) URIs it throws
  `NotSupportedException`.

### Open design question (the deliberate `NotSupportedException`)
Whether a remote URI should be **fetched to a stream** by the abstraction, or **handed to the engine
natively** (Mistral `document_url` / DI `uriSource`), is a real design decision the abstraction should
not silently make. The IO-free overload keeps that decision explicit and also keeps the extension
method free of the analyzer's synchronous-IO and disposal warnings. Validated live: the `data:` URI
path returns the same result as the `DataContent` path (`09-images-and-uris.cs`).

---

## Proposal 3 — rename the core method `GetTextAsync` → `ExtractAsync`

### The gap
The core method is named `GetTextAsync`, but it does not return text — it returns a rich `OcrResult`
whose pages carry **markdown + tables + blocks + figures/images + confidence + language**. "GetText"
undersells the shape and reads like a plain string accessor, which is a misnomer for a
document-analysis primitive.

### The shape
```csharp
// before
Task<OcrResult> GetTextAsync(Stream document, string mediaType, OcrOptions? options = null, ...);
// after
Task<OcrResult> ExtractAsync(Stream document, string mediaType, OcrOptions? options = null, ...);
```

Applies to the `Stream` core method and the `DataContent` / `UriContent` overloads alike (all return
`OcrResult`). `ExtractAsync` is short and domain-accurate; alternatives for maintainer discussion are
`GetDocumentAsync` and `AnalyzeAsync` (recommend `ExtractAsync`).

### Why it's separate from typed extraction
A generic `ExtractAsync<T>` on `IOcrClient` would be a **false generic**: projecting a document to a
typed POCO is an `IChatClient.GetResponseAsync<T>()` capability, not something a document-native OCR
engine can do. So the rename keeps the method returning `OcrResult`; typed extraction stays pure
`IChatClient` composition (see the vision-LLM structured-output pattern below and
`11-vision-structured-output.cs`). Every demo call site already uses `ExtractAsync`; the rename is
staged as a naming-proposal commit for maintainer discussion, not a fait accompli.

---

## Implementation pattern (our provider, **not** a #7588 change) — vision-LLM structured output

`VisionLlmOcrClient` is *our* concrete that wraps an `IChatClient` vision model. MEAI ships first-class
structured output (`IChatClient.GetResponseAsync<T>()` + `ChatResponseFormat.ForJsonSchema<T>()`), so
the client can do better than one freeform-markdown blob — without genericizing `IOcrClient`.

**Two provider-neutral patterns (both in `11-vision-structured-output.cs`, live):**

1. **Structured transcription (opt-in, inside the client).** Set
   `OcrOptions.AdditionalProperties["vision.structured"] = true`. The client asks the model for
   OcrResult-shaped JSON (per-page markdown + tables + figure captions + language + confidence) and
   deserializes straight into `OcrPage`. It **degrades to the freeform path** when the model can't
   honor the schema.
   - Live (survival-kit.pdf): `structured=OFF` → 1 page, no language/confidence; `structured=ON` →
     **10 pages, language=English, confidence=0.99**.
   - Live (dense 2-col USGS doc): the structured attempt exceeded the model's output budget and the
     client **fell back to freeform** — the graceful-degradation path working as designed.
   - Figures from a VLM are **caption-only** (`OcrImage.Content` null) — the archetype Proposal 1's
     nullable `Content` was built for.

2. **User-defined typed extraction (composition, outside the client).** For an arbitrary POCO, do
   **not** grow `IOcrClient`. Reach the inner client via `GetService<IChatClient>()` and call
   `GetResponseAsync<T>()` — the "OCR-then-extract" pipeline (transcribe, then extract).
   - Live: extracted a typed `DocumentSummary` (title / topic / key points) from the transcript.

**Note (file-based apps):** structured output needs a reflection-enabled `JsonSerializerOptions`
(`TypeInfoResolver = new DefaultJsonTypeInfoResolver()`) so arbitrary DTOs get a schema; MEAI's default
options use a source-generated context that only knows built-in AI types.

---

## Where the boundary sits (`IOcrClient` vs `IngestionDocumentReader`)

- **`IOcrClient`** = a *capability*: "turn document bytes into structured text/pages." Providers:
  Mistral OCR, Azure DI, Content Understanding, vision-LLM.
- **`IngestionDocumentReader`** (MEDI) = a *pipeline stage*: "produce an `IngestionDocument`." A reader
  may **compose** an `IOcrClient` (e.g. `PdfPigReader` reads the native text layer and OCRs only the
  pages that need it — `OcrPolicy` decides *when*, the client decides *how*).

`IOcrClient`'s job stays transcription. Structured extraction and enrichment compose *around* it
(`GetResponseAsync<T>()` over the inner chat client, or an enricher stage), never *inside* it.
