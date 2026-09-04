# SDM — Speed Download Manager

A Windows-first desktop download manager built on Avalonia and .NET 10.

SDM downloads a file over several connections at once, resumes a transfer that was
interrupted — including by closing the application or losing power — sorts finished files
into category folders, and accepts links handed to it by a browser over a native
messaging host.

It is under active development. What works and what does not is listed plainly below.

## What works today

**Transfers**
- Multi-part downloads: a range-capable file is split across several connections
  (4 by default) writing into one preallocated file at their own offsets
- Resume from a `.part` file and its sidecar, across restarts and crashes
- Pause, resume, and cancel per transfer
- Retry with exponential backoff and jitter, honouring `Retry-After`
- An idle timeout, so a server that goes silent fails instead of hanging for ever
- Global, per-host, and per-host-connection limits, so a busy site does not earn a 429
- Live speed and ETA, per transfer and in total

**Files**
- Names taken from `Content-Disposition`, sanitised so a hostile header cannot write
  outside the download folder
- An extension added from the server's type when the URL has none, so a Google image
  saved as `images` becomes `images.jpg` and Windows can open it
- Sorted into Documents, Video, Audio, Images, Programs, and Compressed by extension
  first, then by the server's content type
- An optional save dialog, shown before the transfer starts, that reports what the
  server actually said the file is — real name, size, type, and whether it can be resumed

**The application**
- A console-style shell: status and category filters with live counts, a seven-column
  table, and a detail panel with Details, Connections, and History tabs
- A right-click menu on any transfer: open the file, show it in its folder, copy the link,
  and remove the row — keeping what is on disk, or deleting it
- The transfer list is stored in SQLite and restored on the next launch
- A settings screen writing to a user file outside the installation, so settings survive
  a rebuild and an update
- A rolling file log under `%LOCALAPPDATA%\SDM\logs`, written synchronously so the lines
  before a crash reach the disk

**Browser bridge**
- A native messaging host (`SDM.NativeHost`) speaking Chrome's framing over stdin/stdout
- A named pipe to the running application, restricted to the current user; the host never
  downloads anything itself, so there is one engine however many browsers are connected
- A Chrome extension that takes over the browser's downloads, sending the cookies, the
  referring page and the user-agent with them, so a file behind a login still downloads.
  A download is only taken from Chrome once SDM has accepted it. See [extension/](extension/)

## What does not exist yet

- **Video.** No media detection, quality selection, HLS/DASH, or muxing.
- **A speed limit**, scheduling, clipboard monitoring, notifications, and a tray icon.
- **Packaging:** no installer, code signing, or updater.
- Linux and macOS are not validated. Avalonia makes them reachable; nothing more.

## Technology

C# 14, .NET 10, Avalonia UI 12 with compiled bindings, CommunityToolkit.Mvvm,
`Microsoft.Data.Sqlite`, Microsoft dependency injection / configuration / logging,
xUnit.net v3, GitHub Actions on Windows.

## Repository structure

```text
src/
  SDM.Core/            Domain models and download abstractions
  SDM.Application/     Use cases, scheduling, settings, and the bridge protocol
  SDM.Infrastructure/  HTTP engine, partial files, logging, and the named pipe
  SDM.Database/        SQLite persistence and its migrations
  SDM.Desktop/         Avalonia UI and the composition root
  SDM.NativeHost/      Chrome native messaging host
tests/                 Core, Application, Infrastructure, Desktop, NativeHost, Integration
tools/                 Native host registration and acceptance scripts
docs/                  Product scope, architecture, roadmap, and ADRs
```

Dependencies point in one direction only — Core ← Application ← {Infrastructure,
Database} ← Desktop — and `ArchitectureReferenceTests` fails the build if that stops
being true.

## Prerequisites

- .NET SDK 10.0.200, or a later 10.0 feature band, as selected by `global.json`
- Windows 10 or later

## Build and test

```powershell
dotnet restore SDM.sln
dotnet build SDM.sln --configuration Release --no-restore
dotnet test  SDM.sln --configuration Release --no-build
```

Warnings are errors here, and the suite is 202 tests. Tests that need a server run against
a local `HttpListener`, never the public internet.

## Run

```powershell
dotnet run --project src/SDM.Desktop/SDM.Desktop.csproj
```

## Registering the native host

```powershell
.\tools\install-native-host.ps1
```

This writes `com.sdm.host` under `HKCU` for Chrome, Edge, and Brave, pointing at the built
host. `tools\send-native-message.ps1` exercises it without a browser.

Then load the extension: `chrome://extensions` → **Developer mode** → **Load unpacked** →
choose [extension/](extension/). Its id is fixed by a key in its manifest, so the
registration above already names it and keeps working after a reload or a move.

## Documentation

[Roadmap](docs/roadmap.md) — the phase-by-phase plan, and the state of each phase.
[Product scope](docs/product-scope.md) · [Architecture](docs/architecture.md) ·
[Decision records](docs/decisions/)

## License

[MIT](LICENSE).
