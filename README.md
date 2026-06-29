# Zephyr

Zephyr is a fast, keyboard-friendly file manager for Windows 11 — with tabs, split-pane browsing, built-in archive support, and folder locking, all wrapped in a clean, theme-matched interface that actually feels like part of the OS.

<p align="center">
  <img src="ZephyrLook.png" alt="The Zephyr file manager window with its dark theme" width="850">
</p>

## Download

Grab the latest build from the [**Releases**](https://github.com/JP-Discoveries/Zephyr/releases/latest) page — download the `win-x64` zip, unzip it anywhere, and run `Zephyr.exe`. No installer and no .NET runtime required; everything is self-contained.

## Features

### Browsing & navigation
- **Tabbed browsing** with a breadcrumb path bar and full navigation history (back / forward / up).
- **Split / dual-pane view** for working across two locations at once.
- **Dual-pane compare** — highlights which files are identical, unique, newer, or older between the two panes.
- **Tear-off tabs** — drag a tab out into its own window.
- **Details and thumbnail/icon views** with an adjustable thumbnail size.
- **Flat view** to flatten a folder tree and see every file at once.
- **Filter bar** for quickly narrowing the current folder.
- **Search** across the current location — search by name or search inside file contents (grep), with regex support and recursive deep search.
- **Session restore** reopens your tabs and panes on launch.
- **Network locations** — pin UNC paths and mapped drives in the sidebar for quick access.

### File operations
- **Transfer manager** — queued copy/move with live progress, plus pause, resume, and cancel.
- **Undo** for file operations.
- **Recycle Bin and permanent delete** (Shift+Del).
- **Batch rename** for renaming many files at once.
- **Batch attribute editor** — set Read-only, Hidden, System, and Archive flags and timestamps across many files at once, with optional recursion into subfolders.
- **Create NTFS links** — symbolic links, directory junctions, and hard links.
- **Checksum viewer** — compute MD5, SHA-1, and SHA-256 for any file in a single pass.
- **Color labels** — tag files and folders with a color (Red, Orange, Yellow, Green, Blue, Purple, Gray) for quick visual identification.
- **Command palette** for quick, keyboard-driven actions.

### Archives
- **Browse inside** `.zip` and other archives as if they were folders.
- **Compress and extract** with progress, including encrypted (AES) zips.

### Windows integration
- **Win+E integration** — register Zephyr as the default handler so Win+E opens it instead of Explorer.
- **Integrated terminal** — open a terminal at the current folder (Ctrl+`).
- **Shell integration** — native context menus and file icons.
- **Portable device support** (MTP / WPD) for phones and cameras.
- **Cloud sync badges** for OneDrive and other synced folders.
- **Portable mode** — save settings next to the executable instead of `%AppData%`, useful for running from a USB drive.
- **Launch at Windows startup** — optional auto-start when you log in.

### Display & privacy
- **Disk usage analyzer** — scan any folder and visualize space consumption as a treemap.
- **Folder Lock** — gate individual folders behind a password for the session.
- **Preview pane** for images, text, and PDF documents.
- **Thumbnails** with caching for fast, smooth scrolling.
- **Quick Access, bookmarks, and recent files** for jumping to what you use most.
- **Recently interacted highlight** — accent border on items you recently opened, renamed, or created; optionally sort them to the top of the file list.
- **Show/hide hidden & system files** and **file extensions**.
- **Optional folder sizes** in the listing.
- **Light and dark themes** that match the rest of the Windows 11 shell.
- **Configurable startup folder**, launch-maximized, and more.

### Customization
- **Rebindable hotkeys** — reassign any shortcut from Settings → Shortcuts.
- **Customizable toolbar** — choose which action buttons appear and reorder them from Settings → Toolbar.

## Tech stack

- **C# / WPF** on **.NET 10** (`net10.0-windows10.0.22621.0`)
- MVVM via [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Archives: built-in `System.IO.Compression` (plain-zip write), [SharpCompress](https://github.com/adamhathcock/sharpcompress) (read), and [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (encrypted-zip write)
- PDF preview: the built-in Windows engine (`Windows.Data.Pdf`) renders pages; [PdfPig](https://github.com/UglyToad/PdfPig) extracts text
- Drive and device info via [System.Management](https://www.nuget.org/packages/System.Management)
- Tests: [xUnit](https://xunit.net/)

## Requirements

- **Windows 11** (build 22621 / 22H2 or later)
- **To run:** nothing extra — release builds are self-contained and bundle the .NET runtime
- **To build from source:** the [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Building

```sh
dotnet build Zephyr.slnx -c Release
```

Run the tests with:

```sh
dotnet test Zephyr.slnx
```

## Running

```sh
dotnet run --project Zephyr.UI/Zephyr.UI.csproj
```

Or launch the built executable directly:

- **`run.vbs`** — starts Zephyr with no console window (resolves the exe path relative to itself, so it works from any clone location).
- **`run.cmd`** — runs `dotnet run` in a console window, handy for seeing build/runtime output.

## Project layout

| Project | Description |
|---------|-------------|
| `Zephyr.UI` | WPF front end — views, view models, controls, dialogs, themes, and services. |
| `Zephyr.Core` | Core logic — file system, archives, search, security (folder lock), settings, and models. |
| `Zephyr.Tests` | Unit tests. |
