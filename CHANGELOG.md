# Changelog

## 0.2.0

The first release with an installer, and the first that has been through a full review.

### The browser handover catches downloads before Chrome asks

Chrome settles a download's name and, if "ask where to save each file" is on, prompts
while doing it. The extension used to hook `chrome.downloads.onCreated`, which fires after
that has begun — so it could only react to a download the browser had already asked about.
Two prompts for one file.

It now hooks `onDeterminingFilename`, which fires before the prompt, and takes the
download by never letting the name settle. Chrome never asks.

The trade is stated plainly because it is real: the download is cancelled before SDM has
agreed to take it. If SDM refuses, it is handed straight back by re-issuing it, which
starts it again rather than resuming. Three things are refused before anything is
cancelled — a file that answers a POST, anything that throws on the way, and a handover
that goes unanswered for 45 seconds.

### Fixed

- **A mis-answered range no longer corrupts a file.** A segment wrote whatever the server
  sent to its own offset without checking what it had been sent. A range request answered
  `200` with the whole file — which HTTP permits — pasted the beginning of the file into
  the middle of the download, and nothing downstream could catch it: the byte count and
  the file length both came out right, so it was reported as finished.
- **The download engine no longer runs on the interface thread.** Every read, every write,
  the folder walk that looks for a partial file, and the final move of a finished file
  were all being pumped through the dispatcher the window draws on.
- **A transfer queued behind another on the same host no longer fails instantly.** The
  idle clock was started before the connection lease was taken, so the wait counted as
  silence from a server that had not been asked anything yet.
- **Only one copy of SDM runs per user.** Two copies bind the same pipe, open the same
  database, contend for the same log, and can resume the same partial file. A second
  launch now brings the running window forward and exits.
- **The same address cannot be downloaded twice at once.** Both rows wrote into the same
  partial file and both reported the corrupt result as finished.
- **A download saved somewhere other than the default folder resumes there.** It used to
  start again from the beginning, into the wrong folder.
- **The transfer list survives a non-Gregorian calendar.** Timestamps were written
  round-trip and read with the machine's own culture, so an Arabic Windows set to the
  Hijri calendar could not restore the list at all.
- **A row can no longer get stuck on "Downloading" for ever.** Failures the code had not
  thought of became unobserved exceptions, leaving a row with no message and a resume
  button it would not enable.
- **The browser bridge survives a client that says nothing.** It served one connection at
  a time with no deadline, so a native host killed mid-handover held it until SDM was
  restarted.
- **Deleting a downloaded file asks first.** It sat one entry below "Remove from list" in
  a context menu, reachable by the same click that opens the menu.
- **The partial-file record survives a power cut.** It was rewritten in place every two
  seconds, and a truncated one reads back as no record at all — so the download started
  over at the one moment the file exists to survive.
- **The Connections field shows a number.** It was bound to a property nothing ever
  assigned, so it was blank for every transfer, and the status column could only ever say
  "Downloading".
- **The transfer list stops flickering.** It was cleared and refilled whenever any row
  changed status, which reset the scroll position and moved the selection under you.
- **Settings refuse a download folder that cannot be written to**, rather than accepting
  it and failing every download afterwards one at a time.
- The database uses write-ahead logging and waits out a lock instead of losing a row's
  state; an older snapshot can no longer overwrite a newer one.
- The extension no longer records the headers of every request the browser makes. It
  watches only what a download can arrive as, and sweeps what has expired.
- Published builds include the executable Chrome launches. They did not.
- The native messaging manifest lives beside your data rather than in a build output
  folder, which any clean build deleted while leaving the registration pointing at it.

### Added

- An installer. Per-user, no administrator rights, and it registers the browser bridge
  for every Chromium browser it finds.
- Tests for the view models, which had none — 23 of them, over the most stateful code in
  the repository.
- `tools/check-extension.mjs`, which loads the extension against a stub of the browser API
  and drives downloads through it. Three faults reached a browser before it existed, and
  none of them was visible to the build or to the test suite.

### Known limitations

- The browser extension has to be added by hand. Chrome removed the last way to do this
  from outside the browser in version 137, and blocked local installs from the registry in
  version 33. The steps are in the README and take about thirty seconds.
- Connection limits apply on the next start rather than the next download.
- No video detection, speed limiting, scheduling, or automatic updates.
- Windows only. Linux and macOS are reachable but not validated.

## 0.1.0

Not released. Development up to the first working browser handover.
