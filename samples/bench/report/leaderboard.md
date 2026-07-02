# OCR-vs-PdfPig eval — leaderboard

Same content, two encodings (born-digital + scanned) · 6 questions · 11 known facts.
Real vector retrieval held identical across rows. Reference-free structural metrics + NLP F1 vs authored references. No gold accuracy claimed.

## `usgs-petroleum-assessment.pdf`

| Extractor | Pages | Tables | Images | Yield (chars) | Coverage | avg F1 | ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| mistral-ocr | 2 | 0 | 1 | 12657 | 100 % | 0.65 | 6515 |
| pdfpig-native | 2 | 0 | 0 | 11657 | 100 % | 0.65 | 750 |
| pdfpig+ocr-fallback | 2 | 0 | 0 | 11657 | 100 % | 0.64 | 77 |
| vision-llm | 1 | 0 | 0 | 13053 | 100 % | 0.62 | 38230 |
| azure-di | 2 | 3 | 3 | 14847 | 100 % | 0.57 | 8335 |


Takeaway (born-digital): pdfpig-native ties on text (coverage 100 %, F1 0.65) at a fraction of the latency (750ms), but recovers 0 tables / 0 figures. azure-di surfaces 3 table(s) + 3 figure(s) — the structure the naive text layer cannot see.

## `usgs-petroleum-assessment-scanned.pdf`

| Extractor | Pages | Tables | Images | Yield (chars) | Coverage | avg F1 | ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| pdfpig+ocr-fallback | 2 | 0 | 3 | 12777 | 100 % | 0.71 | 5938 |
| vision-llm | 1 | 0 | 0 | 13590 | 100 % | 0.63 | 35877 |
| mistral-ocr | 2 | 0 | 3 | 12777 | 100 % | 0.62 | 6857 |
| azure-di | 2 | 3 | 3 | 14786 | 100 % | 0.58 | 6769 |
| pdfpig-native | 2 | 0 | 0 | 2 | 0 % | 0.21 | 1 |


Takeaway (scanned): pdfpig-native recovers 2 chars / coverage 0 % — the image-only page has no text layer. azure-di recovers 14786 chars, coverage 100 %, 3 table(s) + 3 figure(s): OCR is the ONLY path that reads the page.

