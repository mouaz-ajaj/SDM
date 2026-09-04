<div align="center">

<img src="Logo.png" alt="SDM" width="120">

# SDM — Speed Download Manager

**A download manager for Windows that takes files from your browser and fetches them
faster, resumes them after a crash, and sorts them into folders.**

[![Download](https://img.shields.io/github/v/release/mouaz-ajaj/SDM?label=Download&style=for-the-badge)](https://github.com/mouaz-ajaj/SDM/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/mouaz-ajaj/SDM/ci.yml?branch=main&label=build&style=for-the-badge)](https://github.com/mouaz-ajaj/SDM/actions)
[![License](https://img.shields.io/badge/license-MIT-blue?style=for-the-badge)](LICENSE)

</div>

---

## What it does

- **Downloads over several connections at once.** A file the server lets you request in
  pieces is split across four connections, each writing into its own part of one file.
- **Resumes.** After a pause, a crash, a power cut, or closing the application — it picks
  up from the bytes already on disk rather than starting again.
- **Takes over the browser's downloads.** Click a link in Chrome and it appears in SDM
  instead, with your session, so a file behind a login still downloads.
- **Sorts what it finishes** into Documents, Video, Audio, Images, Programs and
  Compressed.
- **Verifies before it finishes.** A stream that ends early looks exactly like one that
  ended on time, so the bytes are counted against what the server promised, and a short
  file stays a `.part` file rather than becoming a broken archive.

---

## Install

1. **[Download the latest installer](https://github.com/mouaz-ajaj/SDM/releases/latest)** —
   `SDM-Setup-x.y.z.exe`
2. Run it. It installs for you alone, so **it will not ask for an administrator
   password**.
3. That is it. SDM registers itself with every Chromium browser it finds.

> **"Windows protected your PC"**
>
> The installer is not code-signed — a certificate costs a few hundred dollars a year, and
> this is a hobby project. Click **More info → Run anyway**.
>
> If you would rather not, [build it yourself](docs/development.md); it takes two commands.

**Prefer no installer?** Every release also has `SDM-x.y.z-portable.zip`. Unzip it
anywhere and run `SDM.Desktop.exe` — it registers its own browser bridge on first run.

### Uninstalling

Settings → Apps → SDM. It removes the browser registration with it. Your downloads,
settings and transfer list stay in `%LOCALAPPDATA%\SDM`, so reinstalling finds everything
where you left it — delete that folder if you want a clean slate.

---

## Setting up your browser

The installer registers the **bridge** — the piece that lets Chrome talk to SDM —
automatically. The **extension** you have to add by hand, once, and it takes about thirty
seconds.

<details open>
<summary><b>Chrome, Edge, Brave, Vivaldi, Opera — the three steps</b></summary>

<br>

1. Open a new tab and go to **`chrome://extensions`**
   *(Edge: `edge://extensions` — Brave: `brave://extensions`)*

2. Turn on **Developer mode**, top right

3. Click **Load unpacked** and choose the `extension` folder inside where SDM installed:

   ```
   %LOCALAPPDATA%\Programs\SDM\extension
   ```

   Paste that into the folder dialog's address bar and press Enter.

</details>

**To check it worked:** click SDM's icon in the browser toolbar. It should say
`on` three times. Then download something — it should appear in SDM and **not** in Chrome.

<details>
<summary><b>Why can't it install itself?</b></summary>

<br>

Because Chrome does not allow it, and has not for a long time. Installing an extension
from a local folder was blocked for anything outside the Chrome Web Store in **Chrome 33**,
and the last way to do it from outside the browser — the `--load-extension` command line
flag — was **removed in Chrome 137** because it was being used to install malware.

Every download manager has the same problem. IDM's and FDM's extensions are on the Chrome
Web Store, which is why theirs is one click; the store is the only way, and it costs money
and a review. SDM may go there eventually. Until then it is three steps, once.

</details>

---

## Using it

**From the browser.** Just download something. It goes to SDM instead — before Chrome
asks you where to save it.

Or right-click any link, image, video or audio and choose **Download … with SDM**.

**From SDM.** Paste an address into the box at the top and press Add.

**The list.** Every transfer has a right-click menu — open the file, show it in its folder,
copy the link, remove the row. Removing keeps the file unless you ask otherwise, and
deleting a file always asks first.

Select a row and the panel below shows what the server said it was, the connections
carrying it, and everything that has happened to it.

---

## Settings

| | |
|---|---|
| **Download folder** | Where files go. Checked when you save it, so a path on a drive that is not plugged in is refused rather than accepted and failing later. |
| **Sort into category folders** | On by default. Off puts everything in one folder. |
| **Ask where to save** | Off by default. On shows what the server says the file is — its real name, size and type — before the transfer starts. |
| **Transfers at once** | How many downloads run together, in total and per site. |
| **Connections per site** | The limit servers actually enforce. Going higher earns a `429`, not more speed. |
| **Parts per file** | How many connections one file is split across. |
| **Attempts** and **silence limit** | How hard to retry, and how long a silent server has before it counts as failed. |

Connection limits apply the next time SDM starts. Everything else applies to the next
download.

Your settings live in `%LOCALAPPDATA%\SDM\settings.json`, outside the installation, so
they survive updates.

---

## When something is wrong

<details>
<summary><b>Downloads still go to Chrome</b></summary>

<br>

Click SDM's icon in the toolbar and read the **Status** panel.

- **Any of the three says `not started`** — reload the extension at `chrome://extensions`.
- **All three say `on` but nothing reaches SDM** — press **Test connection to SDM**.
  - *"host not found"* → the bridge is not registered. Run
    `%LOCALAPPDATA%\Programs\SDM\SDM.NativeHost.exe --register`
  - *"SDM is not running"* → start SDM. It should start itself; if it does not, the path
    recorded at install has moved — reinstall.
- **A particular site only** — some sites answer only the request their own page made.
  Add that site to the exclusions list in the extension's options and Chrome will keep its
  downloads.

</details>

<details>
<summary><b>A download failed</b></summary>

<br>

Select the row and open **History** — it says what happened and when. Failed transfers
keep what they downloaded, so **Resume** carries on rather than starting again.

</details>

<details>
<summary><b>Something else</b></summary>

<br>

`%LOCALAPPDATA%\SDM\logs` has a file per day. A crash before the window opens is written
to `startup-error.log` in the same folder.

[Open an issue](https://github.com/mouaz-ajaj/SDM/issues) with the relevant lines.

</details>

---

## What it does not do yet

No video detection or stream downloading (HLS/DASH), no speed limiting, no scheduling, no
clipboard monitoring, and no automatic updates — new versions are downloaded from
[Releases](https://github.com/mouaz-ajaj/SDM/releases).

Windows 10 or later, 64-bit. Linux and macOS are reachable — the interface is
cross-platform — but nothing has been tested there.

---

## Building it yourself

Everything about the code, the architecture and the tests is in
**[docs/development.md](docs/development.md)**.

The short version:

```powershell
git clone https://github.com/mouaz-ajaj/SDM.git
cd SDM
dotnet run --project src/SDM.Desktop/SDM.Desktop.csproj
```

---

<div align="center">

[MIT](LICENSE) · [Changelog](CHANGELOG.md) · [Report a problem](https://github.com/mouaz-ajaj/SDM/issues)

</div>
