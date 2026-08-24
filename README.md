# MicaPDF

PDF viewer for Windows with Mica backdrop, zoom/navigation, ink and text annotations, and PDF export. Built with WinUI 3.

## Features

- Mica / Mica Base Alt backdrop (WinUI 3)
- PDF viewing via Windows.Data.Pdf (zoom 50%–500%)
- View modes: single page, double page, cover page, continuous scroll
- Document outline (Chapters sidebar)
- Page labels in the status bar when the PDF defines them
- Recent files on the welcome screen (cover thumbnail, restores page and zoom)
- Pen, highlighter, eraser, and text annotations with undo/redo
- Copy text from the PDF
- Save annotated PDF (PDFsharp export)
- Print
- Settings: theme, language (EN/IT), menu order, toolbar placement, update check
- Optional default PDF handler registration
- Drag-and-drop to open files

## Download

Pre-built releases (self-contained, no .NET install required):

- [Releases](https://github.com/TCDev69/MicaPDF/releases)
- `MicaPDF-Setup-x64.exe` / `MicaPDF-Setup-ARM64.exe` — installer
- `MicaPDF-Portable-x64.zip` / `MicaPDF-Portable-ARM64.zip` — portable build

Each zip includes `INSTALL.txt`, `README.md`, and `CHANGELOG.md`.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- GitHub release builds bundle the .NET runtime (standalone)
- Building from source requires .NET 10 SDK

## Usage

Open a PDF from the menu, recent files, or by drag-and-drop. Use **Edit** for the annotation toolbar. **Save annotated PDF** writes a new PDF with your drawings and text. Settings are under the gear icon in the menu.

## Build from source

Requires .NET 10 SDK and Visual Studio 2022 (or VS Code) with the Windows App SDK workload.

```powershell
dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
dotnet publish -c Release -r win-arm64 -p:Platform=ARM64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

## Technical details

- Framework: WinUI 3 (Windows App SDK)
- Language: C# / .NET 10
- Rendering: Windows.Data.Pdf
- Outline/text index: PdfPig
- Export: PDFsharp

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

GNU General Public License v3.0 (GPLv3). See [LICENSE](LICENSE).
