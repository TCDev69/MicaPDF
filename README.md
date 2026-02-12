# MicaPDF

MicaPDF is a modern PDF viewer designed for Windows 11, featuring a clean user interface that integrates natively with the OS design language using the Mica material. It supports basic PDF viewing capabilities along with pen annotation tools.

## Features

- **Modern Design**: Built with WinUI 3 and the Windows App SDK, utilizing the Mica backdrop for a native Windows 11 look and feel.
- **PDF Viewing**: Fast and reliable PDF rendering powered by the native Windows.Data.Pdf API. Supports zooming (50% to 500%) and smooth scrolling.
- **Annotation Tools**:
  - **Select Mode**: Navigate and interact with the document using mouse or touch.
  - **Pen Mode**: Draw freehand annotations directly on the pages.
  - **Eraser Mode**: Remove existing annotations with precision.
- **Save & Export**: Save annotated pages as high-quality PNG images.
- **Double Page View**: View two pages side-by-side for a book-like reading experience.
- **Navigation**: Quick page jumping, "Next/Previous" controls, and keyboard shortcuts.
- **System Integration**: Can be set as the default PDF handler in Windows.

## Installation

### Installer
The easiest way to install MicaPDF is using the provided installer (setup.exe).
The installer will automatically check for the required .NET Desktop Runtime 8.0 and prompt you to download it if it's missing.

### From Source
To build and run the application from source, you will need the .NET 8.0 SDK and Visual Studio 2022 (or VS Code) with the "Windows App SDK" workload installed.

1.  Clone the repository.
2.  Open the solution in your IDE.
3.  Build and run the MicaPDF project.

You can also publish the executable using the command line:

`powershell
dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained false -p:PublishSingleFile=true
`

## Requirements
*   Windows 10 version 1809 (build 17763) or later
*   .NET Desktop Runtime 8.0

## Usage

### Viewing Documents
Open a PDF file by clicking **Open File** in the navigation menu, or by dragging and dropping a PDF file directly into the application window.

### Tools
Use the sidebar menu to switch between different interaction modes:
*   **Select / Mouse**: Standard mode for scrolling and zooming.
*   **Pen**: Enables drawing mode. Note that scrolling is disabled while drawing to prevent accidental movement.
*   **Eraser**: Allows you to delete strokes by tapping or dragging over them.

### Saving
Current annotations are not saved back into the PDF file structure but are exported as images. Click **Save with Annotations** to export the current page view, including your drawings, as a PNG file.

## Technical Details

*   **Framework**: WinUI 3 (Windows App SDK)
*   **Language**: C# / .NET 8.0
*   **Rendering**: Windows.Data.Pdf
*   **Backdrop**: Mica / Mica Base Alt

## License

This project is licensed under the GNU General Public License v3.0 (GPLv3). See the [LICENSE](LICENSE) file for details.
