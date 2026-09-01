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

### Phase 2.3 — Download list, cancel, concurrency — DONE

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

### Phase 3.1 — Resume — DONE

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

Verified by hand: closing the application mid-transfer left `1GB.bin.part` beside a
sidecar naming the owning URL, its total length and Hetzner's own ETag.

### Phase 3.2 — SQLite persistence — DONE

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

### Phase 3.3 — Multi-part segmented download — DONE

This is where the *Speed* in the name is earned.

**Scope**
- Split a `Range`-capable download into N segments, download in parallel, write into
  one preallocated file at the right offsets.
- Aggregate per-segment progress into one figure.
- Segment count configurable, default 4.
- A per-host connection budget, distinct from the per-host transfer limit: segments
  draw from it, and no single transfer may take the whole of it — one connection is
  held back for every other transfer the host is allowed to run, or a second download
  of the same site would wait for the first to finish rather than share with it.

**Acceptance**
- Measurably faster than single-stream on a server that supports `Range`.
- SHA-256 still matches.
- Pause, resume, and cancel all still work with segments in flight.

---

### Phase 3.4 — Logs that reach the user — DONE

Raised in the first review and left open ever since: the application is built as a
Windows executable, so it has no console. Every line it logged went nowhere unless
stdout happened to be redirected, and a failure during startup left no trace at all.

**Scope**
- A rolling file log under `%LOCALAPPDATA%SDMogs`, written synchronously so the
  entries immediately before a crash are on disk rather than in a queue.
- Daily files, a size cap that rolls, and a retention window.
- `CrashLog`, which records a startup failure without the dependency container — the
  container being what failed is exactly the case that used to vanish.

**Acceptance**
- Launching the executable with no redirection leaves a readable log on disk.
- The log can be opened while the application is still running.
- Old logs are removed; a growing log rolls instead of filling the disk.

---

### Phase 3.5 — File types and category folders — DONE

**Scope**
- `FileCategories` classifies by extension first, then the server's Content-Type — the
  extension is what the user sees and what Windows opens the file with, and plenty of
  download URLs end in an opaque id with no extension at all.
- `IDownloadLayout` in the application layer decides the folder; the engine asks once
  the name and type are settled. Sorting is a policy, not something transfer code knows.
- The engine records the Content-Type; the database gains a column for it by appending
  a second migration.
- Unrecognised files stay at the top rather than filling an "Other" folder nobody opens.

**Acceptance**
- A .pdf lands in `DownloadsDocuments`, an .iso in `Compressed`, an .exe in `Programs`.
- A URL with no extension is sorted by its Content-Type.
- A partial file inside a category folder is still found and resumed — looking only at
  the top level would silently restart the download.

---

### Phase 3.6 — Ask where to save — DONE

**Scope**
- `IDownloadEngine.ProbeAsync` asks the server what a URL is — name, size, type, range
  support — with a one-byte range request, without downloading the body.
- A save dialog pre-filled with the real name, reached through Avalonia's storage
  provider behind an interface so the view model does not depend on a window.
- An explicitly chosen folder and name skip category sorting and skip the "name (1)"
  rule: the system dialog has already asked about replacing, and second-guessing it
  would ignore what the user just said.
- `Downloads:AskWhereToSave`, off by default.

**Acceptance**
- A URL ending in an opaque id offers its real name in the dialog, not "download".
- Dismissing the dialog starts nothing and leaves the address in the box.
- A chosen destination lands exactly there, sorting or no sorting.

### Phase 3.7 — Settings and the save dialog — DONE

Brought forward ahead of the shell: both are separate windows, so neither waits on the
main view being rebuilt, and the user asked for them first.

**Scope**
- A user settings file layered over the shipped one, outside the installation, watched
  so a saved value applies to the next download rather than the next launch.
- `IUserSettingsStore` writes it, preserving every section the settings screen does not
  manage: the file belongs to the user, not to one screen.
- Options are read through `IOptionsMonitor` wherever a value is read per call.
  Connection limits become semaphores at startup and still need a restart, which the
  screen says plainly instead of pretending otherwise.
- SDM's own save dialog replaces the system picker: it shows what the server said the
  file is — real name, size, type, whether it can be resumed — plus a folder with Browse.

**Acceptance**
- Settings survive a rebuild and an update.
- Saving one value leaves a hand-written section in the same file untouched.
- The save dialog shows the real name for a URL that ends in an opaque id.

### Phase 3.8 — The console shell — DONE

The chosen design, built: a sidebar of status filters and categories with live counts, a
real table, and a detail panel with Details, Connections and History tabs.

**Scope**
- The engine now reports per-connection progress alongside the total. An unsplit
  transfer reports one segment, so the interface needs no special case for it.
- The detail panel sits at the bottom rather than the right: 960 px minus a sidebar
  minus a right panel leaves too little for a seven-column table, and the table is the
  point of this design. Across the bottom the details get three columns instead of one.
- Status and category are two ways of narrowing the same list, so choosing one clears
  the other rather than combining into an empty result.
- Each row keeps its own history, appended at the transitions that already exist.

**Acceptance**
- Nine rows visible at once; the sidebar counts follow status changes without polling.
- The Connections tab shows one bar per part with its byte range.
- Selecting a row keeps its selection while other rows change status.

### Phase 3.10 — The right-click menu — DONE

The cheapest feature left and the most noticeable: until now a finished download could not
be opened from SDM at all, and a single row could not be removed.

**Scope**
- Open the file, show it in its folder, copy the link, remove the row.
- `ISystemShell` — the one seam between a view model and the desktop environment, so a row
  can offer all three without holding a window.
- Removal is two entries, not one. `TransferRemoval` is an enum rather than a bool: the
  difference between tidying a list and destroying a file should not be told apart by
  reading a `true` at the call site. This is the same fault that made "Clear finished"
  delete paused transfers.
- Right-clicking selects the row, so the menu and the detail panel below always describe
  the same transfer.

**Acceptance**
- Open and Open folder are unavailable until there is a file and a path to open.
- Removing a running transfer stops it first rather than leaving it writing unseen.
- "Remove from list" leaves the file; only "Remove and delete file" removes it.

### Phase 3.11 — Verify the file, and say so — DONE

Raised by a screenshot: a transfer sat at 100% still labelled "Downloading", with a speed
beside it, and the question behind it — is anything checked once the bytes stop?

Nothing was. `Complete` renamed the partial file and reported success without ever
comparing what arrived against what the server said it would send.

**Scope**
- The partial file is checked before it is promoted: the byte count against
  `Content-Length`, and the count against the length actually on disk. A short transfer
  fails as transient, so the partial survives and the retry resumes rather than restarts.
- A `Verifying` callback, because the gap the screenshot caught is real: on a large file
  the flush, the move and whatever scans downloads are not instant, and the row used to
  spend that time claiming to download at a speed that had stopped being true.
- The percentage is rounded **down** while running. 99.6% displayed as "100%", and a row
  reading 100% with a live speed looks stuck rather than nearly finished.

**Acceptance**
- A server that promises a length and delivers half of it never produces a file at the
  destination name, and its partial file survives.
- Verification is announced before the file takes its real name, not after.

**Not covered:** HTTP carries no checksum for an arbitrary file, so there is nothing to
compare the contents against. Only the length is verifiable. A server that sends the right
number of wrong bytes cannot be caught here.

### Phase 3.9 — Browser-facing settings (planned)

The browser pane of the settings screen — bridge status, per-browser extension state,
and the takeover toggles. Built alongside Track C, since none of it can be honest
before the bridge exists.

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

DONE. Verified end to end: `toolssend-native-message.ps1` framed a download request,
the host carried it over the pipe, and the file landed in `DownloadsCompressed`.
IPC decided: a named pipe scoped to the current user. The host starts SDM when it is
not running and never transfers anything itself, so there is one engine however many
browsers are connected.

### Phase 4.2 — Minimal extension: "Send to SDM" — DONE

**Scope:** `extension/` folder, MV3 manifest, background service worker, a right-click
context menu on links.

**Acceptance:** right-click any link in Chrome → "Download with SDM" → the job appears
in the running app within a second.

The extension id is **pinned** rather than left to Chrome. An unpacked extension's id is
derived from the folder it was loaded from, so moving the repository would change it and
silently break the host registration — and the browser reports that only as "host not
found". `manifest.json` therefore carries the public half of a keypair, which fixes the id
at `efcijjodjgojhelobljfkbigkndfeobe` on every machine and after every reload. The private
half is deliberately outside the repository, in `%LOCALAPPDATA%\SDM\keys`; it is needed
only to pack a `.crx` with this same id.

Verified end to end without a browser, since Chrome only has to reproduce what the host
already does: the id derived from the shipped manifest matches the one registered in
`allowed_origins`, the self test passes both checks, and a framed download request reached
the engine — `Browser handed over …` at 17:13:44.038, `Completed …Downloads\Documents\README`
at 17:13:44.922, sorted by content type since the URL has no extension.

**Fixed here:** the bridge answered "accepted" to a request that arrived while SDM was
closing, and then dropped it — the application detaches its handler before disposing the
bridge. The extension would have reported a download that never existed. Seen for real
during this phase, and now refused with an error instead.

**Still needs a person:** loading the folder once at `chrome://extensions`. Nothing in this
repository can do that.

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
| License | FFmpeg is LGPL/GPL depending on build; shipping its binaries constrains the choice. | **Resolved: MIT.** FFmpeg and yt-dlp are run as separate executables rather than linked, so their terms apply to their own binaries. Revisit only if either is ever linked into the application itself. |
| Host ↔ app IPC | Named pipe vs. loopback HTTP. Locks in at Phase 4.1. | **Resolved: a named pipe** scoped to the current user. No port, unreachable from the network, and restricted by the operating system. |
| Installer technology | MSIX vs. Inno Setup. Affects native-host registration. | Open |
| Smart App Control | Was enforced on the dev machine and refused to load locally built assemblies, so neither the app nor most tests could run there. Turned off; all 39 tests and the application now run locally. | Resolved |

## Non-goals (unchanged from the product scope)

BitTorrent, a cloud backend, mobile apps, DRM circumvention, and guaranteed support
for every video site remain out of scope. See [product-scope.md](product-scope.md).
