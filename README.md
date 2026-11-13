# Document Studio (PdfStudio)

A fast, native Windows multi-format document studio built with **.NET 10** (`net10.0-windows`), **Avalonia UI**, **SkiaSharp**, direct **PDFium** `[LibraryImport]` P/Invoke rendering, **PdfSharpCore**, **DocumentFormat.OpenXml**, and **AvaloniaEdit**—supporting **PDF** (`.pdf`), **Spreadsheets** (`.xlsx`, `.csv`, `.tsv`), **Word Documents** (`.docx`), **Markdown, Code & Plain Text** (`.md`, `.txt`, `.json`, `.xml`, `.log`), and **Images** (`.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp`) in a unified browser-style tab workspace.

---

## Features

- **Crisp Hardware-Accelerated Rendering** — Direct `pdfium.dll` zero-copy rendering into `SKBitmap` at exact physical device pixels with cursor-anchored `Ctrl + Scroll` zooming, fit-to-width, and fit-to-page modes.
- **Multi-Tab Workspace & Single-Instance Launch** — Open multiple PDFs across tabs (`Ctrl+Tab` / `Ctrl+W`), drag-and-drop from File Explorer, or launch files from Explorer into a new tab in the running window via Named Pipe coordination.
- **Full-Text Search (`Ctrl+F`)** — Character-level bounding box highlights across pages with vivid active-match highlighting, smooth scroll centering, and `F3` / `Shift+F3` stepping.
- **Page Organizer & Structural Editing** — Lazy-loaded thumbnail sidebar with drag-and-drop page reordering, 90° left/right rotation, page deletion, blank page insertion, page extraction, and multi-PDF merging with full Undo/Redo (`Ctrl+Z` / `Ctrl+Y`).
- **Annotations, Redaction, Forms & Signatures** — Highlight, Underline, Freehand Draw, Text Box, and Sticky Note tools with contextual color swatches, stroke widths, and font sizes; permanent content-stripping **Redact** tool; interactive AcroForm field filling; drawn or PNG **Signature** stamping; and password protection.
- **Ergonomics & Hardening** — Spacebar + Drag and Middle-Mouse panning, clickable `Page X of Y` jump input, right-click canvas/thumbnail context menus, Recent Files start view, unsaved-changes tab close prompt, atomic temp-file writes, and `%LocalAppData%\PdfStudio` settings and crash logging.

---

## Themes

Press `Ctrl+Shift+T` (or click the **Theme** button in the top bar) to switch between 6 built-in themes persisted in `%LocalAppData%\PdfStudio\settings.json`:

| Mode | Presets |
| :--- | :--- |
| **Dark** | `Obsidian` *(default)*, `Midnight Nord`, `Tokyo Night`, `Monokai Dimmed` |
| **Light** | `Studio Light`, `Warm Paper` |

---

## Keyboard Shortcuts

### File, Workspace & Search
| Shortcut | Action |
| :--- | :--- |
| `Ctrl+O` | Open PDF |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / Save As |
| `Ctrl+W` | Close active tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / Previous tab |
| `Ctrl+F` | Open Find bar |
| `F3` / `Shift+F3` | Next / Previous search match |
| `Ctrl+K` | Open Command Palette |
| `Ctrl+Shift+T` | Open Theme Picker |
| `Esc` | Close modal, Find bar, or return to Select tool |

### Navigation & Zoom
| Shortcut | Action |
| :--- | :--- |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | Zoom In / Zoom Out / Actual Size (100%) |
| `Ctrl+1` / `Ctrl+2` | Fit Width / Fit Page |
| `Ctrl + Mouse Wheel` | Cursor-anchored smooth zoom |
| `Space + Drag` / `Middle-Mouse Drag` | Pan document canvas |
| `PageUp` / `PageDown` / `Home` / `End` | Page and document navigation |
| `[` / `]` | Toggle Pages sidebar / Inspector panel |

### Tools, Editing & Pages
| Shortcut | Action |
| :--- | :--- |
| `V` / `H` | Select tool / Hand tool |
| `U` / `P` / `T` / `N` / `R` | Highlight / Draw / Text / Note / Redact |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo |
| `Ctrl+Shift+R` / `Ctrl+Shift+L` | Rotate page right / left |
| `Del` | Delete selected annotation or page |

---

## Build & Run from Source

### Prerequisites
- Windows 10 / 11 (`win-x64`)
- [.NET 10 SDK](https://dotnet.microsoft.com/) (`net10.0-windows`)

Native `pdfium.dll` (`win-x64`) is restored automatically via the `bblanchon.PDFium.Win32` NuGet package.

### Run
```powershell
dotnet run --project src\PdfStudio.App
```

### Build & Test
```powershell
dotnet build PdfStudio.slnx
dotnet test PdfStudio.slnx
```

### Publish Standalone Single-File Executable
```powershell
dotnet publish src\PdfStudio.App\PdfStudio.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o C:\Dist\PdfStudio-Portable
```

---

## Solution Structure

- `src/PdfStudio.Core` — Document models, annotations, form fields, LRU bitmap cache, and non-destructive `StructuralEditPlan`.
- `src/PdfStudio.Rendering.Pdfium` — High-speed native `pdfium.dll` `[LibraryImport]` P/Invoke renderer, outline reader, and character-level text extractor.
- `src/PdfStudio.Editing.PdfSharp` — `PdfSharpCore` structural saving, merging, extraction, true content redaction, form flattening, and AES encryption.
- `src/PdfStudio.App` — Avalonia UI desktop shell, Skia viewport (`PdfCanvasControl`), themes, ViewModels, single-instance coordinator, and crash logger.
- `tests/PdfStudio.Tests` — xUnit and Avalonia Headless automated test suite.

