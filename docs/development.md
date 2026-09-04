# Working on SDM

Everything about the code. If you want to *use* SDM, the [README](../README.md) is the
place; nothing here is needed to run it.

## Prerequisites

- **.NET SDK 10.0.200**, or a later 10.0 feature band — `global.json` pins the band
- **Windows 10 or later** — the engine is portable, the browser registration is not
- **Node** — only to run the extension check
- **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** — only to build the installer

## Build, test, run

```powershell
dotnet restore SDM.sln
dotnet build   SDM.sln --configuration Release
dotnet test    SDM.sln --configuration Release --no-build
node tools/check-extension.mjs

dotnet run --project src/SDM.Desktop/SDM.Desktop.csproj
```

Warnings are errors. The suite is **215 tests** and everything that needs a server runs
against a local `HttpListener` — nothing in it reaches the public internet, and nothing in
it depends on the machine it runs on.

## How it fits together

```text
src/
  SDM.Core/            Domain models and download abstractions
  SDM.Application/     Use cases, scheduling, settings, the bridge protocol
  SDM.Infrastructure/  HTTP engine, partial files, logging, the named pipe
  SDM.Database/        SQLite persistence and its migrations
  SDM.Desktop/         Avalonia interface and the composition root
  SDM.NativeHost/      Chrome native messaging host
extension/             The browser extension
installer/             Inno Setup script
tools/                 Registration script and the extension check
docs/                  Product scope, architecture, decisions
```

Dependencies point one way only:

```text
Core ← Application ← { Infrastructure, Database } ← Desktop
```

`ArchitectureReferenceTests` fails the build if that stops being true, so the layering is
enforced rather than described.

## Things that are the way they are for a reason

Some decisions look odd until you know what they cost to learn. These are the ones most
likely to be undone by accident.

### The engine must not run on the interface thread

`DownloadItemViewModel` starts the transfer through `Task.Run`, and the library layers
configure their awaits. Remove either and the whole engine resumes on the dispatcher: every
80 KB read, every write, the folder walk that looks for a partial file, and the final move
of a finished file all get pumped through the thread the window draws on.

The four callbacks the engine raises — `Planned`, `Started`, `Retrying`, `Verifying` — are
plain delegates invoked wherever the engine is standing, so they go through `IUiThread`.
`Progress<T>` marshals itself; those do not.

`IUiThread` is injected rather than reached for, and that is not ceremony. A test process
pumps no dispatcher, so posted work never runs — every assertion about what those
callbacks set was passing by never being reached. The tests were green because the code
under test had not executed.

### A segment must prove it got the range it asked for

`SegmentedTransfer.EnsureAnswersTheRange` checks the status and the `Content-Range` before
opening the stream. Ranges are optional in HTTP: a host answered by several machines can
honour one on the connection that discovered it could be split, and ignore it on the next.

Written at the segment's own offset, that response is the beginning of the file pasted into
the middle of the download — and nothing downstream can catch it, because the byte count
and the file length both come out exactly right. It has to fail there or not at all.

### The idle clock starts after the connection lease

A transfer waits for its host's connections for as long as the other transfers hold them.
Start the clock first and that wait counts as silence from a server that has not been asked
anything yet, so a queued transfer fails the instant it is granted a connection — and every
retry queues and dies the same way.

### The partial-file sidecar is written whole and moved

It is the only record of a split transfer's progress; the file itself is pre-allocated at
full size, so its length says nothing. Written in place — as it was — a machine losing
power mid-write leaves a truncated one, which reads back as no record at all, at the one
moment the file exists to survive.

### Row saves carry a stamp that cannot go backwards

Rows persist themselves without being awaited, from several transfers at once, so writes
do not reach SQLite in the order they were taken. The upsert refuses a snapshot older than
the row it would replace, and `NextRevision` guarantees a row's own stamps only ever
increase — a machine correcting its clock against a time server would otherwise silently
stop saving.

### Timestamps are read with the invariant culture

They are written round-trip. Read with the machine's own culture, an Arabic Windows set to
the Hijri calendar reads "2026" as a Hijri year and the whole list fails to restore.

## The browser extension

Read [extension/README.md](../extension/README.md) first — it explains the handover in
detail. Two things matter most:

**The hook is `onDeterminingFilename`, not `onCreated`.** The later one fires after Chrome
has begun settling a download's name, which is when it shows its save dialog. Taking a
download at the earlier one means never letting the name settle, so Chrome never asks. The
cost is that the download is cancelled before SDM has agreed to take it, which is why
several things are refused *before* anything is cancelled.

**Nothing in the build or the test suite reads these files.** Three faults reached a
browser before `tools/check-extension.mjs` existed — an unrecognised manifest key, a
diagnostic that reported working listeners as dead, and a variable read before its
declaration that killed the service worker outright. `node --check` catches none of them;
they are all runtime facts.

The check loads `background.js` against a stub of the browser API — which is what
registering a service worker does — and drives four downloads through it: one accepted, one
refused, one answering a POST, and one where the host never replies. **Run it before
touching that folder.**

### Trying extension changes

`chrome://extensions` → the reload arrow on SDM's card. The extension id is fixed by the
`key` in its manifest, so it survives reloads and moves and the host registration keeps
working.

The **Status** panel on the options page says which listeners registered, and **Recent
activity** logs each handover.

## The native messaging host

`SDM.NativeHost.exe` speaks Chrome's framing on stdin and stdout and forwards to the
running application over a named pipe. It never downloads anything, so there is one engine
however many browsers are connected.

**Nothing may ever write to its stdout.** The browser reads that stream as a
length-prefixed protocol, and one stray line desynchronises it permanently. Diagnostics go
to stderr or to a file.

```powershell
# Registers the bridge for every Chromium browser with a profile folder
.\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe --register

# Checks the framing and whether SDM is running, without starting it
.\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe --selftest

# Exercises the pipe without a browser
.\tools\send-native-message.ps1
```

Registration is in C# rather than in a script for two reasons that are not preference:
script execution is disabled by policy on a great many Windows machines, and the manifest
holds absolute paths — a user whose name is not in the installer's code page has a profile
folder that a script's ANSI file writing turns into nonsense.

## Cutting a release

```powershell
# 1. Version, in both places
#      Directory.Build.props   <Version>
#      extension/manifest.json "version"
# 2. Write the CHANGELOG entry
# 3. Tag and push

git tag v0.3.0
git push origin v0.3.0
```

The tag is the whole trigger. `.github/workflows/release.yml` builds, tests, checks the
extension, publishes, runs Inno Setup, and opens a **draft** release with the installer and
a portable zip — draft, so a release is always something somebody chose to publish.

To build the installer by hand:

```powershell
dotnet publish src/SDM.Desktop/SDM.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\sdm.iss
```

### Why the publish is a publish and not a copy

There are two executables, and a self-contained SDM carries its own runtime while a host
copied from an ordinary build does not. That host would look for a .NET installed on the
machine — which, on the machine of somebody who installed a self-contained application, is
exactly what is not there. It would fail to start with no message, and the browser would
report "host not found" for a completely different reason than usual.

So `SDM.Desktop.csproj` invokes the host's *publish* target with its own runtime identifier
and self-contained setting, into its own publish folder.

### Why the installer is per user

SDM already keeps its settings, database, logs and browser registration per user. A
machine-wide installation would be installed once and configured separately by every person
who ran it. Per user also means no administrator rights, which is one fewer reason for
somebody not to try it.

## Documentation

[Product scope](product-scope.md) · [Architecture](architecture.md) ·
[Roadmap](roadmap.md) · [Decision records](decisions/)
