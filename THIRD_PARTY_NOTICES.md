# Third-party notices

The root MIT license applies to PaperQuay's own source. NuGet package versions and hashes are pinned in the package lock files. Build dependencies come from the package sources configured in NuGet.

| Component | License and source |
| --- | --- |
| NAPS2 SDK, GDI integration, and Windows worker | Copyright 2009–2026 NAPS2 Contributors. LGPL-2.1-or-later. The [SDK license](licenses/NAPS2-SDK-LGPL-2.1.txt) and [source revision used by 1.6.1](https://github.com/cyanfish/naps2/tree/c6c762214293097c522fc1b7656f106fbb66a7f2) are available. |
| NAPS2 WIA and NTwain | MIT. See [NAPS2.Wia](https://www.nuget.org/packages/NAPS2.Wia/2.0.3) and [NAPS2.NTwain](https://www.nuget.org/packages/NAPS2.NTwain/1.0.1). |
| NAPS2 PDFsharp fork | MIT. See [NAPS2.PdfSharp](https://www.nuget.org/packages/NAPS2.PdfSharp/1.0.2). |
| NAPS2 PDFium and Tesseract binary packages | Apache-2.0 package licenses, with upstream notices for their bundled components. See [PDFium binaries](https://www.nuget.org/packages/NAPS2.Pdfium.Binaries/1.3.0) and [Tesseract binaries](https://www.nuget.org/packages/NAPS2.Tesseract.Binaries/1.5.0). |
| German and English OCR and orientation models | Apache-2.0. See the [pinned tessdata_best revision](https://github.com/tesseract-ocr/tessdata_best/tree/e12c65a915945e4c28e237a9b52bc4a8f39a0cec) and the [model license](src/PaperQuay/tessdata/LICENSE). The bootstrap script checks each model's SHA-256. |

This repository distributes source. A redistributor of a compiled bundle must account for the licenses and notices of the bundled dependencies, including the LGPL requirements for any redistributed NAPS2 libraries and worker binaries.
