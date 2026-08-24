# MicaPDF

PDF viewer for Windows 11 with Mica backdrop, zoom/navigation, and ink/text annotations. Saves annotations back into a PDF file.

## Features

- WinUI 3 / Windows App SDK with Mica Base Alt
- PDF viewing via Windows.Data.Pdf (zoom 50%–500%, continuous and double-page modes)
- Pen, highlighter, eraser, and text annotations
- Save annotated PDF
- Optional default PDF handler registration
- English and Italian UI

## Installation

### Installer

Use `setup.exe` / the release installer. It checks for .NET Desktop Runtime 10.0 if needed.

### From source

Requires .NET 10 SDK and Visual Studio 2022 (or VS Code) with the Windows App SDK workload.

```powershell
dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained false -p:PublishSingleFile=true
```

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET Desktop Runtime 10.0

## Usage

Open a PDF from the menu or by drag-and-drop. Use **Edit** for the annotation bar. **Save annotated PDF** writes a new PDF with your drawings and text.

## Technical details

- Framework: WinUI 3 (Windows App SDK)
- Language: C# / .NET 10
- Rendering: Windows.Data.Pdf
- Export: PDFsharp
- Backdrop: Mica / Mica Base Alt

## License

GNU General Public License v3.0 (GPLv3). See [LICENSE](LICENSE).
