# Abstract

**One interface for every OCR engine: provider-neutral document parsing for .NET**

Getting a PDF into clean, structured text is the messiest part of any RAG or document pipeline, and
today it locks you to one vendor. Mistral OCR, Azure Document Intelligence, Azure Content
Understanding, and vision LLMs each solve the same job behind a different API, so switching engines is
a rewrite instead of a config change.

This talk introduces `IOcrClient`, a provider-neutral seam for document parsing that follows the exact
pattern .NET already uses for `IChatClient` and `IEmbeddingGenerator`: one interface, any engine, swap
with a line. We run four live engines through a single loop with identical code, then draw the line
that matters — `IOcrClient` is a *capability*, `IngestionDocumentReader` is a *pipeline stage*, and one
small `OcrDocumentReader` bridges them into a real Microsoft.Extensions.DataIngestion (MEDI) pipeline.
From there we carry the page number through chunking so answers cite their source page, show the same
seam composed a second way by the PdfPig reader (digital text first, OCR only the pages that need it),
and close with an end-to-end run from PDF to a page-cited answer.

Everything is grounded on real runs you can reproduce from the sample repo — and the samples run on
the real `dotnet/extensions` code, packed locally, not a mock. You will leave knowing where OCR stops
and the pipeline begins, how to keep your document pipeline free of vendor lock-in, and how a set of
open pull requests across `dotnet/extensions` and `CommunityToolkit/AI` make this a first-class
building block.

Audience: .NET developers building RAG, document, or agent pipelines. No prior OCR experience needed.
