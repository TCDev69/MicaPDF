# Changelog

All notable changes to MicaPDF are listed here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.2.0] - 2026-09-03

### Added

- Configurable maximum zoom in Settings (50%–500%, default 150%)
- Navigation menu grouped into sections (File, Zoom, Pages, Annotations)
- Keyboard shortcut hints on menu items (EN/IT)
- "Indexing…" status while building the find text index
- Settings panel redesigned with Windows 11-style SettingsExpander/SettingsCard (Community Toolkit)
- Export and import settings as JSON (Advanced section)
- About section: version badge, GitHub repository link, license link
- Menu item search/filter in Settings
- Per-menu-item icons in the settings list
- Reset menu order to default button
- InfoBar feedback for settings actions (success/error with auto-dismiss)
- Load diagnostics and raster size calculator for large-document performance

### Changed

- PDF rendering and page cache tuned for lower memory use (raster cap, overlay pooling, compressed PNG page cache with small decoded LRU)
- Text index and find/zoom logic refactored for smoother navigation
- Settings UI moved from code-built controls to declarative XAML layout
- Settings descriptions added for all sections and options (EN/IT)
- Update check errors now shown as error severity in the settings InfoBar
- GitHub repository setting moved from Updates to Advanced section

### Fixed

- Duplicate PropertyChanged handlers on menu items causing double auto-save

## [2.1.0] - 2026-08-27

### Added

- Find in document (Ctrl+F) with match navigation
- Unlock password-protected PDFs
- Local annotation sidecar: ink/text autosaved under LocalAppData and restored on reopen
- Fit height / fit width zoom (menu and toolbar)
- Rotating diagnostic logs (keeps the last 3 files under LocalAppData)
- Unit tests for zoom fit, logging trim, and annotation keys (`MicaPDF.Tests`)

### Changed

- Settings UI moved to XAML (`SettingsPanel.xaml`)
- PdfPig updated to 0.1.15
- Minimum OS called out as Windows 10 build 19041+
- Viewer session and zoom-fit logic extracted for clearer layout handling

### Fixed

- Page cache and text index edge cases while searching / zooming

## [2.0.0] - 2026-08-25

Major update. UI, annotations, and settings were largely rewritten since 1.3.0.

### Added

- Settings panel: theme, language (EN/IT), menu layout, floating toolbar position
- Document outline (Chapters sidebar)
- Page labels in navigation when the PDF defines them
- Recent files on the welcome screen with cover thumbnails (page and zoom restored on reopen)
- Undo/redo for ink and text annotations
- Text tool with bold, italic, and color options
- Copy selected text from the PDF
- GitHub update check (Settings → Updates)
- In-app localization (English / Italian)
- Print support
- Continuous scroll, double-page, and cover-page viewing modes

### Changed

- Annotated export writes a PDF (PDFsharp), not PNG snapshots
- Project renamed PDFViewer → MicaPDF
- Target runtime: .NET 10 (was .NET 8)
- Release builds are self-contained (no separate .NET install needed)

### Fixed

- Outline loading and PDF open flow refactored for large documents

## [1.3.0] - 2025

- Print support
- Navigation controls improvements
- Bumped to .NET 10

See [v1.3.0](https://github.com/TCDev69/MicaPDF/releases/tag/v1.3.0) for earlier releases.
