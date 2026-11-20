# PaperQuay

PaperQuay is a Windows desktop app for turning a stack of paper into searchable PDFs. It scans through WIA or TWAIN, corrects images and reads text locally, then files the PDFs to a folder you choose.

## Scan a batch

1. Install the scanner manufacturer's Windows driver. In **Scan**, choose **Find scanners** and select a WIA or TWAIN connection.
2. Choose **One PDF per load** or **Mixed stack**. Set the folder for that profile under **Save folder…**. Both profiles start in your Windows Documents folder.
3. Load the feeder and choose **Scan & queue**. The default is A4 color duplex at 300 dpi. OCR starts as soon as both sides of a sheet are stored when a worker is available.
4. Follow progress in **History**. A missing side, uncertain split, or suggested crop appears under **Needs a decision**. Review the original beside the corrected image, adjust the split or crop, restore an omitted side if needed, and approve.

PaperQuay straightens and rotates scans, evens out paper backgrounds, and can omit a blank side or a mirrored impression that matches the other side. In mixed stacks, it uses page numbers, document titles, references, and blank separator sheets. German and English OCR run with Tesseract. Weak OCR appears as a warning, and you can retry recognition for an individual page.

The queue lives under `%LOCALAPPDATA%\PaperQuay\Queue`. It keeps original scanner images while a batch is pending and for seven days after successful filing. If a destination is unavailable, the local queue keeps the PDF and retries delivery. Selecting a network folder sends the finished PDFs to that folder. Original images and OCR working files are handled locally.

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
