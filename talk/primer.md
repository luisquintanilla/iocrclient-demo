# Primer — OCR &amp; document ingestion in 90 seconds

The up-front grounding so nobody in the room is lost before the demo starts. Two primer slides open
the deck (before "The problem"). Keep it to ~90 seconds; it's a shared vocabulary, not a lecture.

## What to say

**What OCR is.** OCR / document parsing turns a document's *bytes* — a born-digital PDF, a scan, a
photo — into *structured text*: pages, headings, tables, figures. It's step zero of every document-AI
pipeline. If you can't get clean, structured text out of the document, nothing downstream works.

**The pipeline vocabulary** (used for the rest of the talk):

> **extract → structure → chunk → retrieve → answer**

OCR owns the first two (extract the text, recover the structure). Everything after — chunk, retrieve,
answer — is the pipeline. The whole talk is about the seam between those two halves.

**The two engine archetypes** (this is the one distinction to remember):

- **Document-native** (Mistral OCR, Azure Document Intelligence, Content Understanding): send the
  whole document in one call, get structured pages back — with native tables, confidence, and figure
  images. Twelve pages in, twelve structured pages out.
- **Transcribe-by-seeing** (a vision LLM): reads the page like a person and hands back one blob of
  Markdown. No native page model, no structured tables, higher variance — but it can read anything.

Same job, very different fidelity, latency, and cost. The point of the whole talk is serving *both*
archetypes through *one* contract, so the caller chooses on fidelity and cost, not on API shape.

**Where OCR sits** (second primer slide, diagram `assets/diagrams/d2-data-flow.svg`): the document
enters `IOcrClient` on the left and becomes an `OcrResult` of pages — markdown, tables, blocks,
figure images, confidence. A thin reader maps that into the MEDI pipeline, which chunks, retrieves,
and answers with page-level citations. A dashed line separates the OCR *capability* from the pipeline
*stage*. That line is the whole idea; the deck walks it left to right.

## Why it's up front

Engineers land in the room at different depths. Ninety seconds of shared vocabulary — the pipeline
verbs and the two archetypes — means the payoff slide (four engines, one loop) lands for everyone, not
just the people who already do RAG.
