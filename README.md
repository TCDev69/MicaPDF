# MicaPDF

A modern PDF viewer for Windows 11 with translucent Mica background and pen annotation support.

## ✨ Features

- 🎨 **Mica Translucent Background** - Native integration with Windows 11 design system
- 📄 **PDF Viewing** - High-quality PDF rendering with zoom support (50% - 500%)
- �️ **Pen Annotations** - Draw and annotate directly on PDF pages
- 💾 **Save Annotations** - Export annotated pages as PNG images
- ⏮️⏭️ **Page Navigation** - Easy navigation with keyboard shortcuts
- 🖱️ **Touch & Pan** - Drag to move around the document with mouse or touch
- 📂 **Drag & Drop** - Drop PDF files directly into the window
- 🎯 **Go to Page** - Quick jump to any page number
- 🌐 **Microsoft Store Style UI** - Modern, clean navigation menu
- 📱 **Custom Titlebar** - Integrated Mica titlebar with file name display
- 💭 **Remember State** - Saves menu open/closed preference
- 🔗 **File Association** - Set as default PDF viewer in Windows

## 📋 Requirements

- Windows 11 (recommended for full Mica effect)
- Windows 10 version 1809 (build 17763) or higher
- .NET 8.0 Runtime (included in self-contained builds)

## 🚀 Installation

### Option 1: Download Release (Recommended)
1. Download and run `MicaPDF-Setup-x.exe` from the Releases page

### Option 2: Build from Source
```powershell
# Build debug version
dotnet build -p:Platform=x64

# Build release (single-file executable)
dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=true
```

## 🎯 Usage

### Opening PDF Files
- **File Menu**: Click "Open File" in the side menu
- **Drag & Drop**: Drag a PDF file into the window
- **Command Line**: `MicaPDF.exe "path\to\file.pdf"`
- **Windows Explorer**: Right-click PDF → Open with → MicaPDF

### Navigation
- **Next/Previous Page**: Use menu buttons or navigate with scroll
- **Go to Page**: Click "Go to page..." and enter page number (press Enter)
- **Zoom**: Use Zoom In/Out buttons or scroll wheel

### Pen Annotations
1. Click "Enable Pen" in the menu
2. Draw on the PDF with mouse, pen, or touch
3. Click "Disable Pen" to return to navigation mode
4. Click "Clear Annotations" to remove all drawings
5. Click "Save with Annotations" to export as PNG

### Keyboard Shortcuts
- **Enter**: In "Go to page" dialog, jumps to entered page

## 🛠️ Technical Details

- **Framework**: WinUI 3 with .NET 8.0
- **Windows App SDK**: 1.5.240802000
- **PDF Rendering**: Windows.Data.Pdf API (native Windows)
- **Backdrop**: Mica BaseAlt for enhanced translucency
- **UI Pattern**: NavigationView (Microsoft Store style)
- **Annotations**: Vector-based Polyline drawing

## 📦 Project Structure

```
PDFViewer/
├── App.xaml/cs              # Application entry point with command-line support
├── MainWindow.xaml/cs       # Main window with all functionality
├── PDFViewer.csproj         # Project configuration
├── app.manifest             # Windows compatibility settings
└── README.md                # This file
```

## 🔧 Set as Default PDF Viewer

### Method 1: Windows Settings
1. Right-click any PDF file
2. Select "Open with" > "Choose another app"
3. Check "Always use this app to open .pdf files"
4. Select MicaPDF (browse to exe if not listed)

### Method 2: Windows Settings App
1. Open Settings > Apps > Default apps
2. Search for "MicaPDF"
3. Set as default for .pdf files

## 📝 Notes

- **Annotation Export**: Annotations are saved as PNG images, not embedded in PDF
- **Page-by-Page**: Each page with annotations must be saved individually
- **File Naming**: Saved files are named `[original_name]_[number].png`

## 🤝 Contributing

This is a personal project, but suggestions and feedback are welcome!

## 📜 License

This project is provided as-is for educational and personal use.

## 🎨 Credits

- Built with WinUI 3 and Windows App SDK
- Mica design system by Microsoft
- PDF rendering via Windows.Data.Pdf API


## Tecnologie utilizzate

- WinUI 3
- Windows App SDK
- Windows.Data.Pdf API
- Mica backdrop (sistema di sfondo traslucido di Windows 11)