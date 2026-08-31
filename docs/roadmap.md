# Roadmap

This roadmap replaces the week-based staging in the original planning document with
**vertical slices**. Every phase ends with an application that runs and does one more
real thing than it did before. The unit of work is a *session* (2–4 focused hours),
not a week.

## How to run a phase

1. Hand the agent **one phase at a time**. Never two.
2. Ask it to inspect the current code before writing anything.
3. Give it the phase's *Scope*, *Out of scope*, and *Files* sections verbatim.
4. A phase is not closed until: the app runs, `dotnet test` is green, and the
   acceptance criteria are demonstrated.
5. Commit after every closed phase.
6. If a bug appears, give it its own phase. Do not stack features on top of it.
7. No broad refactoring during a feature phase without a stated reason.

## Definition of done (every phase)

- `dotnet build SDM.sln -c Release` — 0 warnings, 0 errors (warnings are errors here)
- `dotnet test SDM.sln -c Release` — green
- `ArchitectureReferenceTests` still green (the layer boundary held)
- The acceptance criteria below are demonstrated by hand at least once
- One commit, pushed

---

## Track A — Make it real (4 sessions)

After Phase 2.2 you have a program you can actually run and use. That milestone
matters more than anything after it.

### Phase 2.0 — Toolchain — DONE

Pin the SDK to the version actually installed and make CI match it.

**Acceptance:** `dotnet build SDM.sln -c Release` succeeds with no workaround.

### Phase 2.1 — HTTP download engine (headless) — DONE

**Goal:** download a file over HTTP(S) to disk, with progress, cancellable. No UI.

**Scope**
- `SDM.Core`: a `DownloadProgress` record (bytes received, total bytes if known).
  Extend `IDownloadEngine` only as far as this phase actually requires.
- `SDM.Infrastructure`: `HttpDownloadEngine` — takes `HttpClient` via
  `IHttpClientFactory`, streams the response into a `FileStream`, reports
  `IProgress<DownloadProgress>`, honors `CancellationToken`.
- Add `Microsoft.Extensions.Http` to `Directory.Packages.props`.
- `AddSdmInfrastructure()` registers `AddHttpClient` and `IDownloadEngine`.
- New test project `tests/SDM.Infrastructure.Tests`.

**Out of scope:** resume, multi-part, retries, persistence, UI, queue.

**Files:** `src/SDM.Core/Downloads/`, `src/SDM.Infrastructure/`,
`Directory.Packages.props`, `tests/SDM.Infrastructure.Tests/`, `SDM.sln`.
Do not touch `src/SDM.Desktop/` or `src/SDM.Database/`.

**Acceptance**
- Tests run against a local `HttpListener`, never the public internet.
- 5 MB download → SHA-256 of the written file matches the served bytes.
- Cancellation mid-download throws `OperationCanceledException` and leaves no
  half-written file at the destination path.
- A 404 surfaces as a meaningful exception, not a silent empty file.
- Progress is reported more than once and the final report equals the file length.

### Phase 2.2 — First real UI — DONE

**Goal:** paste a URL into the app, press Download, watch it finish. This is the
phase where SDM stops being a shell.

Verified against a live server: a GitHub release archive downloaded to the Downloads
folder, named from the response's `Content-Disposition` rather than the URL's
meaningless last segment, and the archive passed an integrity check.

**Scope**
- `SDM.Application`: a `StartDownloadUseCase`. The UI calls this, never
  `HttpDownloadEngine` directly.
- `MainWindowViewModel`: `Url`, `DownloadCommand` (`RelayCommand` from
  CommunityToolkit), `Progress`, `StatusText`. Real observable properties this time.
- `MainWindow.axaml`: URL `TextBox`, Download `Button`, `ProgressBar`. Keep the
  existing dark visual language — this is an addition, not a redesign.
- Destination: the user's Downloads folder. Filename from `Content-Disposition`,
  falling back to the URL path segment.
- Fix `ApplicationInfoService.Version` to read the entry assembly's
  `InformationalVersion` rather than `SDM.Application`'s own version.

**Out of scope:** a list of downloads, cancel, pause, resume, settings, persistence.

**Acceptance**
- Run the app, paste a real 20–50 MB URL, the bar moves smoothly and the file lands
  in Downloads at the correct size.
- The window stays responsive for the whole download.
- An invalid or non-HTTP URL shows an inline error and does not crash.
- The destination filename is sanitized — a hostile `Content-Disposition` cannot
  write outside the Downloads folder.

### Phase 2.3 — Download list, cancel, concurrency

**Scope**
- `ObservableCollection<DownloadItemViewModel>` replacing the single progress bar.
- Per-item cancel button backed by a per-item `CancellationTokenSource`.
- Bounded concurrency (`SemaphoreSlim`), default 3 simultaneous downloads.
- Per-item speed (MB/s) and ETA.

**Acceptance**
- Three concurrent downloads, cancel one, the other two continue unaffected.
- Speed and ETA update at least once per second and are plausible.
- Closing the window cancels in-flight downloads cleanly with no orphaned files.

### Phase 2.4 — Survive a hostile server — DONE

Added after a real run: three transfers aimed at one speed-test host earned
`429 Too Many Requests`, and SDM gave up permanently on a failure that means
"come back later".

**Scope**
- Per-host concurrency alongside the global limit. Servers cap connections per
  client, so a global limit alone cannot prevent a 429.
- `DownloadFailedException` carrying the status code, `Retry-After`, and whether
  the failure is worth retrying.
- Retry with exponential backoff and jitter, honouring `Retry-After` up to a cap.
- An idle timeout, since `HttpClient.Timeout` is disabled and a silent server
  would otherwise hang a transfer for ever.

**Retry only applies while nothing has been transferred.** Until Phase 3.1 can
resume, retrying after 900 MB would discard those 900 MB and start again. Once
resume exists, this restriction should be lifted.

**Acceptance**
- A 429 with `Retry-After: 5` waits five seconds and retries; a 404 does not.
- Five transfers to one host never exceed the per-host limit; three transfers to
  three different hosts all run at once.
- A server that sends headers then goes silent fails on the idle clock.

---

## Track B — Make it reliable (3 sessions)

### Phase 3.1 — Resume

**Scope**
- Probe `Accept-Ranges`; download into a `.part` file; on resume send
  `Range: bytes=<existing length>-` and append.
- Lift the "no bytes transferred" restriction on retry from Phase 2.4: a retry can
  now resume instead of restarting.
- Pause button alongside cancel. Pause keeps the `.part`; cancel deletes it.
- Servers that do not support `Range`: fall back to restart, and say so in the UI.

**Acceptance**
- Pause mid-download, resume, final SHA-256 matches the complete file.
- Kill the process mid-download, reopen, resume completes correctly.
- A `Range`-less server degrades gracefully instead of producing a corrupt file.

### Phase 3.2 — SQLite persistence

**Scope**
- `SDM.Database`: schema, migration on startup, `IDownloadRepository`
  (the contract lives in Core or Application; the implementation stays here).
- Persist job state on every meaningful transition, not on a timer.
- Restore the list on startup.

**Acceptance**
- Close the app with 2 active and 1 completed download; reopen; all three are listed
  with correct state, and the two active ones resume.
- The database file lives under `%LOCALAPPDATA%\SDM\`, not next to the executable.
- `ArchitectureReferenceTests` still rejects SQLite packages in Core and Application.

### Phase 3.3 — Multi-part segmented download

This is where the *Speed* in the name is earned.

**Scope**
- Split a `Range`-capable download into N segments, download in parallel, write into
  one preallocated file at the right offsets.
- Aggregate per-segment progress into one figure.
- Segment count configurable, default 4.

**Acceptance**
- Measurably faster than single-stream on a server that supports `Range`.
- SHA-256 still matches.
- Pause, resume, and cancel all still work with segments in flight.

---

## Track C — Browser integration (4–6 sessions)

The hardest track. It is here — rather than earlier — because it hands URLs to a
mature engine instead of a fragile one.

### Phase 4.1 — Native Messaging Host (standalone)

**Scope**
- New `src/SDM.NativeHost` console project.
- Chrome native messaging framing: 4-byte little-endian length prefix, then JSON,
  over stdin/stdout. Nothing else may ever be written to stdout.
- Registry registration under
  `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.sdm.host`, via a script.
- A `--selftest` mode so it can be exercised without a browser.
- **Decide here:** how the host reaches the running desktop app. A named pipe is the
  natural choice. The host must never start a second copy of the download engine.

**Out of scope:** the extension itself.

**Acceptance:** a script pipes a framed JSON message in and reads a valid framed
response out; the registry key exists and points at the right executable.

### Phase 4.2 — Minimal extension: "Send to SDM"

**Scope:** `extension/` folder, MV3 manifest, background service worker, a right-click
context menu on links.

**Acceptance:** right-click any link in Chrome → "Download with SDM" → the job appears
in the running app within a second.

### Phase 4.3 — Intercept browser downloads

**Scope:** `chrome.downloads` interception — cancel the browser's download, hand the
URL to SDM. An options page with a master toggle.

**Acceptance:** clicking a `.zip` link downloads in SDM, not Chrome. Toggling the
option off restores normal browser behavior immediately.

### Phase 4.4 — Cookies, referer, user-agent

**Scope:** forward the request context so authenticated and referer-gated downloads
work.

**Acceptance:** a file behind a login downloads successfully in SDM.

---

## Track D — Later, in rough priority order

- **Video via yt-dlp.** Detect media, list qualities, download and mux. Integrating
  yt-dlp covers the overwhelming majority of sites for a fraction of the cost of
  writing an HLS/DASH parser. Write a parser only after hitting a concrete case
  yt-dlp does not cover.
- **Settings and UX:** default folder, categories, speed limit, notifications, tray.
- **Packaging:** installer, code signing, updater.
- **Cross-platform:** Linux and macOS validation.

---

## Open decisions

| Decision | Why it matters | Status |
|---|---|---|
| License | FFmpeg is LGPL/GPL depending on build; shipping its binaries constrains the choice. Decide before Track D. | Open |
| Host ↔ app IPC | Named pipe vs. loopback HTTP. Locks in at Phase 4.1. | Open |
| Installer technology | MSIX vs. Inno Setup. Affects native-host registration. | Open |
| Smart App Control | Was enforced on the dev machine and refused to load locally built assemblies, so neither the app nor most tests could run there. Turned off; all 39 tests and the application now run locally. | Resolved |

## Non-goals (unchanged from the product scope)

BitTorrent, a cloud backend, mobile apps, DRM circumvention, and guaranteed support
for every video site remain out of scope. See [product-scope.md](product-scope.md).
