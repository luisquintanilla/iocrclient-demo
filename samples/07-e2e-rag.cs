#:project proofs/FullE2EProof.csproj

// Reuse the acceptance path instead of maintaining a second RAG implementation.
// Default: native PdfPig + real MEDI/SqliteVec + explicitly labelled AI doubles.
// Live: --mode live --provider all --allow-live
return await FullE2E.RunAsync(args);
