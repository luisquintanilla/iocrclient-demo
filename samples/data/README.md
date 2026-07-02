# Sample documents

Two documents, chosen to exercise different points on the OCR/ingestion spectrum.

## `survival-kit.pdf`

A simple, born-digital, single-column document. The baseline case: text extracts cleanly, so a
"naive" PdfPig text pass and an OCR engine land close together. Good for the first, simplest runs.

## `usgs-petroleum-assessment.pdf`

The complex case: **tables + raster figures + a two-column layout**. Used to show where OCR earns its
keep over naive text extraction, and to exercise the image/figure extraction path (`OcrOptions.IncludeImages`
-> `OcrPage.Images`) across the document-native engines.

- **Title:** *Assessment of Continuous Oil and Gas Resources of the Timan-Pechora Basin Province,
  Russia, 2018* (USGS National and Global Petroleum Assessment fact sheet, FS 2018-3050).
- **Source:** U.S. Geological Survey — https://pubs.usgs.gov/fs/2018/3050/fs20183050.pdf
- **License:** Public domain. As a work of the U.S. federal government it is not subject to copyright
  protection in the United States (17 U.S.C. 105) and is freely redistributable.
- Two pages, a two-column layout, a resource-assessment data table, and embedded raster figures
  (a location map and a chart), which keeps OCR cost modest while still being structurally rich.

## `usgs-petroleum-assessment-scanned.pdf`

The **scanned / image-only** case: the same public-domain USGS fact sheet with every page **rasterized
to an image and no text layer**. This is where OCR earns its keep on its *primary* axis — native PdfPig
text extraction returns essentially nothing (0 characters), while an OCR engine recovers the full page
content. It is the honest counterpart to the born-digital docs above, so the eval can report the
per-document story: *born-digital → native text ties OCR on words but OCR adds tables/figures; scanned →
OCR wins text recovery outright.*

- **Derived from** `usgs-petroleum-assessment.pdf` by rendering each page to a 150-DPI grayscale image
  and repackaging as an image-only PDF (no embedded text). Same source, same license.
- **License:** Public domain (17 U.S.C. 105), same as the source fact sheet; rasterizing a public-domain
  work does not create a new copyright.
- Two pages, image-only. Verified: PdfPig / native text extraction yields 0 characters.
