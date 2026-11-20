# Contributing to PaperQuay

Build and run the checks described in the [README](README.md). Keep fixes focused and include a reproducible check for regressions in image correction, grouping, or queue behavior. The existing check app can generate and process synthetic fixtures without a scanner.

For scanner issues, include the Windows version, scanner model, selected WIA or TWAIN connection, paper source, and DPI. For an OCR, grouping, or duplex issue, describe the expected result, actual result, and which sides contained independent marks. A synthetic sample helps reproduce the problem. Review screenshots and logs for private information before sharing.

Generated PDFs, scanner captures, queue data, and model downloads belong outside Git. Store local check output under `artifacts/`.
