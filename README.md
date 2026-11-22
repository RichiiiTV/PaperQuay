# PaperQuay

PaperQuay is a Windows desktop app for turning a stack of paper into searchable PDFs. It scans through WIA or TWAIN, corrects images and reads text locally, then files the PDFs to a folder you choose.

## Scan a batch

1. Install the scanner manufacturer's Windows driver. In **Scan**, choose **Find scanners** and select a WIA or TWAIN connection.
2. Choose **Automatic split**, **One PDF per load**, or **Fixed sheet count**. Fixed counts refer to physical sheets, so two sides stay together even if one is blank. Set the destination under **Save folder…**. Use **Save profile…** to reuse these choices along with scanner, paper, color, and OCR settings.
3. Set **Paper size** and **Color mode**, then choose **Scan & queue**. The default is A4 color duplex at 300 dpi. **Scan for review** uses the same settings. OCR starts as soon as both sides of a sheet are stored when a worker is available.
4. Follow pending batches in **Queue** and use **Batch actions…** for delivery and batch maintenance. A missing side, uncertain split, or suggested crop appears under **Needs a decision**. For an uncertain split, review shows the previous and current corrected pages side by side. Choose **Split here** or **Keep together**, or use **Move up** and **Move down** to reorder pages. **OCR highlights** shows individual words the local OCR model was less confident about.

Review displays the analyzed pages and proposed groups together. **Show skipped pages** reveals pages skipped during review. **Restore omitted side…** retrieves pages already skipped by the queue before review.

**Scan for review** and imported files create a local draft as their pages arrive. PaperQuay saves page edits and splits as you make them. After reopening the app, use **Needs a decision** to resume the draft. Use **Queue → Batch actions… → Change destination** to change where the batch will go. **Approve & file** stages the PDFs locally and delivers them when the destination is available.

Choose **Clear history** to clear the list and permanently delete the verified PDFs listed in the confirmation. Older PDFs without a recorded fingerprint stay in their destination folder when their history records are cleared. Changed or locked files stay in History for retry. **Open PDF** opens a verified local copy, and **Open folder** opens the folder with the saved file. Pending batches continue in **Queue**. PaperQuay keeps scanner originals under its normal retention policy.

If the scanner reports a double feed or jam, reload the affected sheets and select **Retry** to continue the current batch. If a duplex capture stopped after just one side, PaperQuay opens the partial capture for review instead of assuming the next image is the missing back.

For hardware with automatic re-feeding, select the TWAIN connection and **Use TWAIN scanner settings**. Enable **Separation Retry** or the equivalent option in your manufacturer's dialog when offered. Match the dialog's simplex or duplex setting to PaperQuay's **Sides** setting and leave driver blank-page removal off so PaperQuay receives both sides of each duplex sheet. The Canon DR-C225 detects double feeds and stops for reloading.

PaperQuay straightens and rotates scans and evens out paper backgrounds. **Remove empty pages** also recognizes otherwise empty paper with gradual shading, scattered scanner specks, or a thin feeder streak along the edge. For duplex scans it can recognize binder-hole shadows and fold lines on an empty reverse. Select **Keep empty page** or **Restore omitted side…** in review to include a skipped page. **Remove duplex show-through** separately checks for a mirrored impression that matches the other side. Automatic splitting uses page numbers, document references, letter openings and closings, and blank separator sheets. German and English OCR run with Tesseract. Weak OCR appears as a warning, and you can retry recognition for an individual page.

PaperQuay can request Color, Grayscale, or Black & white from the driver. Grayscale keeps shades of gray. Black & white captures high-contrast pages in two tones.

Paper presets include A4, A5, A6, US Letter, US Legal, US Executive, US Statement (Half Letter), and the B sizes below. The dimensions are listed as width × height. B6R is B6 rotated by 90°, with the long edge entering the feeder first. Available scan areas and color modes depend on the scanner and driver.

| Size | ISO | Japanese (JIS) |
| --- | --- | --- |
| B5 | 176 × 250 mm | 182 × 257 mm |
| B6 | 125 × 176 mm | 128 × 182 mm |
| B6R | 176 × 125 mm | 182 × 128 mm |

The queue lives under `%LOCALAPPDATA%\PaperQuay\Queue`. It keeps original scanner images while a batch is pending and for seven days after successful filing or skipping an entirely empty batch. The rebuildable processing cache for a finished batch is eligible for cleanup after an hour. If a destination is unavailable, the local queue keeps the PDF and retries delivery while another destination can continue. Original images and OCR working files are handled locally.

PaperQuay uses its own queue and settings. If you used the earlier Scan Workbench, choose your destination again in PaperQuay. Your earlier scans remain available through Scan Workbench and their delivery folders.

## Build

Use a Windows x64 machine with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and PowerShell. Initial setup downloads pinned OCR models and NuGet packages. A physical scanner is optional for the checks.

```powershell
./scripts/Get-OcrModels.ps1
dotnet restore PaperQuay.slnx --locked-mode
dotnet build PaperQuay.slnx -c Release --no-restore
dotnet run --project src/PaperQuay -c Release --no-build
```

For a local Windows build you can run outside the repository, publish the app folder. It requires the x64 .NET 10 Desktop Runtime.

```powershell
dotnet publish src/PaperQuay -c Release --no-restore -o artifacts/PaperQuay-win-x64
```

To build the self-contained Windows x64 bundle with its accompanying licenses, run the following from the same source revision and SDK version.

```powershell
./scripts/Publish.ps1
```

After a successful publish, older PaperQuay bundles in this checkout's `artifacts` folder go to the Windows Recycle Bin. A running older version stays available until it is closed and you publish again.

## Checks

```powershell
./scripts/Test.ps1
```

The checks cover faint marks, duplex show-through, crops, PDF sizing, and orientation. To test queue recovery and delivery using generated fixtures, also install Python 3.12 and run the following.

```powershell
python -m pip install -r tests/PaperQuay.Checks/requirements.txt
./scripts/Test.ps1 -IncludeQueue
```

The [contribution guide](CONTRIBUTING.md) describes how to report scanner and OCR problems. GitHub Actions runs the hardware-independent checks on Windows.

## License

PaperQuay is available under the [MIT License](LICENSE). Its dependencies and OCR models use their own licenses. See [Third-party notices](THIRD_PARTY_NOTICES.md).
