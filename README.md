# Zephyr

A modern file manager for Windows 11, built with C# and WPF on .NET 10. Zephyr pairs a clean, fully theme-matched interface with a fast, tabbed browsing experience.

<p align="center">
  <img src="ZephyrLook.png" alt="The Zephyr file manager window with its dark theme" width="850">
</p>

## Features

- **Tabbed browsing** with a breadcrumb path bar and full navigation history (back / forward / up).
- **Archive support** — browse inside `.zip` and other archives as if they were folders, plus compress and extract with progress, including encrypted (AES) zips.
- **Folder Lock** — gate individual folders behind a password for the session.
- **Search** across the current location with configurable options.
- **Preview pane** for images, text, and PDF documents.
- **Thumbnails** with caching for fast, smooth scrolling.
- **Command palette** for quick keyboard-driven actions.
- **Batch rename** for renaming many files at once.
- **Quick Access, bookmarks, and recent files** for jumping to what you use most.
- **Shell integration** — native context menus and file icons.
- **Portable device support** (MTP / WPD) for phones and cameras.
- **Light and dark themes** that match the rest of the Windows 11 shell.

## Tech stack

- **C# / WPF** on **.NET 10** (`net10.0-windows10.0.22621.0`)
- MVVM via [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- Archives: [SharpCompress](https://github.com/adamhathcock/sharpcompress) (read) + [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (encrypted-zip write)
- PDF rendering: [PdfPig](https://github.com/UglyToad/PdfPig)

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 11.

```sh
dotnet build -c Release
```

## Running

```sh
dotnet run --project Zephyr.UI/Zephyr.UI.csproj
```

Or launch the built executable without a console window via `run.vbs` (it resolves the path relative to itself, so it works from any clone location).

## Project layout

| Project | Description |
|---------|-------------|
| `Zephyr.UI` | WPF front end — views, view models, controls, dialogs, themes, and services. |
| `Zephyr.Core` | Core logic — file system, archives, search, security (folder lock), settings, and models. |
| `Zephyr.Tests` | Unit tests. |
