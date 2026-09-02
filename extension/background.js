"use strict";

// The host Chrome launches on our behalf. It has to match the "name" in the host manifest
// and the registry key that tools/install-native-host.ps1 writes; nothing here can work if
// those three disagree, and Chrome's only complaint will be "host not found".
const HOST = "com.sdm.host";

// Whether SDM takes over the browser's own downloads. Read on every download rather than
// cached, so turning it off in the options page takes effect on the very next click.
const TAKEOVER = "takeOverDownloads";

// Hostnames the browser keeps for itself.
const EXCLUDED = "excludedSites";

// One entry per kind of thing that can be downloaded, rather than one entry that guesses.
//
// A right-click on an image inside a link reports both a link and a source, and the first
// version of this file preferred the link — which turned "download this image" into
// downloading the product page wrapped around it. There is no rule that gets this right:
// on a thumbnail linking to the full picture the image is wrong, and on an image linking
// to an article the link is wrong. Chrome does not guess either, which is why its own menu
// offers "Save link as…" and "Save image as…" side by side. So does this one: on a plain
// link only the first appears, and on a linked image both do.
const MENUS = [
  { id: "sdm-link", title: "Download link with SDM", contexts: ["link"], from: "linkUrl" },
  { id: "sdm-image", title: "Download image with SDM", contexts: ["image"], from: "srcUrl" },
  { id: "sdm-video", title: "Download video with SDM", contexts: ["video"], from: "srcUrl" },
  { id: "sdm-audio", title: "Download audio with SDM", contexts: ["audio"], from: "srcUrl" },
];

// A service worker is stopped whenever it is idle, so nothing may live in a variable
// between events. Context menu entries are stored by the browser rather than by us, so
// they are rebuilt from scratch — removeAll first, because creating an id that already
// exists fails, and an extension reloaded during development would otherwise lose its menu.
function install() {
  chrome.contextMenus.removeAll(() => {
    for (const menu of MENUS) {
      chrome.contextMenus.create({ id: menu.id, title: menu.title, contexts: menu.contexts });
    }
  });
}

chrome.runtime.onInstalled.addListener(install);
chrome.runtime.onStartup.addListener(install);

chrome.contextMenus.onClicked.addListener((info, tab) => {
  const menu = MENUS.find((candidate) => candidate.id === info.menuItemId);

  if (!menu) {
    return;
  }

  // Each entry reads the field its own context is about, so nothing is inferred from
  // which fields happen to be present.
  const url = info[menu.from];

  if (url) {
    // A right-clicked link the browser was never asked to fetch has no request to copy,
    // unless the page happened to load it already.
    headersFor(url)
      .then((headers) => handOver(url, tab && tab.url, headers))
      .then((reply) => {
        if (!reply.ok) {
          notify("SDM did not take the download", reply.message);
        }
      });
  }
});

// ---------------------------------------------------------------------------
// Watching what the browser actually sends
// ---------------------------------------------------------------------------

// Sending a cookie, a referer and a user-agent is a guess about which three headers
// matter. It is often right and sometimes badly wrong: an application's own API answers
// the request its page made, and what makes that request acceptable may be a header nobody
// outside the site could name — a client build id, a workspace id, a token that was never a
// cookie. Guessing produced a 403 on a file that other download managers fetch happily,
// because they do this instead: watch the real request go out and copy all of it.
//
// Observation only. MV3 forbids blocking webRequest listeners, and nothing here needs one.
//
// Kept in chrome.storage.session, not in a variable. A manifest v3 service worker is
// killed after about thirty seconds of inactivity, taking every variable with it — so a
// header set captured in memory was routinely gone by the time the download it belonged to
// arrived, and the handover silently fell back to the three guessed headers. That is why a
// protected download still answered 403 after the capture was added: the capture worked,
// and then evaporated. storage.session survives the worker and is cleared when the browser
// closes, which is the right lifetime for a request header anyway.
const RECENT_MS = 120_000;

chrome.webRequest.onSendHeaders.addListener(
  (details) => {
    if (details.requestHeaders) {
      remember(details.url, details.requestHeaders);
    }
  },
  { urls: ["http://*/*", "https://*/*"] },

  // extraHeaders is required for Cookie and the Sec-* family: without it Chrome hides
  // exactly the headers that decide whether a protected download is allowed.
  ["requestHeaders", "extraHeaders"]
);

function keyFor(url) {
  return "req:" + url;
}

async function remember(url, headers) {
  const captured = {};

  for (const header of headers) {
    if (typeof header.value === "string") {
      captured[header.name] = header.value;
    }
  }

  try {
    await chrome.storage.session.set({ [keyFor(url)]: { headers: captured, at: Date.now() } });
  } catch (error) {
    // Session storage has a size cap. Losing one capture is not worth failing a download.
  }
}

// Tries the final URL and the original, because a download that was redirected reports one
// of each and the request was watched under whichever came first.
async function headersFor(...urls) {
  for (const url of urls) {
    if (!url) {
      continue;
    }

    try {
      const stored = await chrome.storage.session.get(keyFor(url));
      const entry = stored[keyFor(url)];

      if (entry && Date.now() - entry.at < RECENT_MS) {
        log("copied", Object.keys(entry.headers).length, "headers from the real request");
        return entry.headers;
      }
    } catch (error) {
      // Fall through to the next candidate.
    }
  }

  log("no captured headers for this download; falling back to cookie and referer alone");
  return undefined;
}

function log(...parts) {
  console.log("[SDM]", ...parts);
}

// ---------------------------------------------------------------------------
// Taking over the browser's own downloads
// ---------------------------------------------------------------------------

chrome.downloads.onCreated.addListener((item) => {
  takeOver(item).catch((error) => notify("SDM could not take the download", String(error)));
});

async function takeOver(item) {
  if (!canTakeOver(item)) {
    return;
  }

  // Paused before anything else is awaited, and before the settings are even read.
  //
  // Every await here is time Chrome spends downloading, and the handover is far from
  // instant: it launches a process, which waits on a pipe, which may start SDM. A small
  // file finishes inside that window. The first version read a setting from storage before
  // pausing, and that alone was enough to lose the race — the browser had the whole file
  // before SDM was asked, so cancelling it did nothing and both copies arrived.
  //
  // Paused rather than cancelled, because SDM has not agreed to anything yet. Pausing
  // stops the bytes but keeps the download alive, so a failed handover can simply resume
  // it. Cancelling first would mean one broken component silently breaks every download in
  // the browser.
  const paused = await chrome.downloads.pause(item.id).then(() => true, () => false);
  const url = item.finalUrl || item.url;

  log(paused ? "paused, handing over:" : "too fast to pause, handing over anyway:", url);

  if (!(await enabled()) || (await excluded(url))) {
    log("left to Chrome (turned off, or an excluded site):", url);

    if (paused) {
      await chrome.downloads.resume(item.id).catch(() => {});
    }

    return;
  }

  // Handed over even when the pause came too late. An earlier version gave up here, on the
  // grounds that one file from the browser beats two — but the point of the setting is
  // that downloads go to SDM, and "except the quick ones" is not something anyone asked
  // for. Chrome's copy is removed below, once SDM has agreed to take it.
  const reply = await handOver(url, item.referrer, await headersFor(url, item.url));

  if (!reply.ok) {
    log("SDM refused, Chrome keeps it:", reply.message);

    if (paused) {
      await chrome.downloads.resume(item.id).catch(() => {});
    }

    notify("SDM did not take the download", reply.message + " Chrome is downloading it instead.");
    return;
  }

  // Cancelling deletes a part-downloaded file; removeFile deletes a finished one. Which
  // applies depends on whether the pause won its race, and both are harmless when they do
  // not apply.
  await chrome.downloads.cancel(item.id).catch(() => {});
  await chrome.downloads.removeFile(item.id).catch(() => {});

  // Removes the entry from the browser's own list, which would otherwise show a cancelled
  // download beside the working one.
  await chrome.downloads.erase({ id: item.id }).catch(() => {});

  log("SDM took it:", url);
}

// Sites whose downloads stay with the browser. Some downloads cannot be reproduced from
// outside it at all — an endpoint that answers only a request the page itself made, with
// headers no other program can supply, refuses SDM with 403 however complete the session
// it is given. Rather than pretend otherwise, those sites can be named here and left alone.
async function excluded(url) {
  const stored = await chrome.storage.local.get(EXCLUDED);
  const list = stored[EXCLUDED];

  if (!Array.isArray(list) || list.length === 0) {
    return false;
  }

  try {
    const host = new URL(url).hostname.toLowerCase();

    // "claude.ai" also covers "api.claude.ai", the way a person naming a site expects.
    return list.some((entry) => host === entry || host.endsWith("." + entry));
  } catch (error) {
    return false;
  }
}

// Only what SDM can actually fetch on its own. A blob: or data: URL exists nowhere but
// inside that page, and handing one over would cancel a download that nothing else can
// then perform.
function canTakeOver(item) {
  const url = item.finalUrl || item.url || "";

  return item.state === "in_progress"
    && (url.startsWith("https://") || url.startsWith("http://"));
}

async function enabled() {
  const stored = await chrome.storage.local.get(TAKEOVER);

  // On by default: someone who installs a download manager's extension is asking for its
  // download manager. The options page turns it off, and the pause-first handover above
  // means a failure falls back to Chrome rather than losing the file.
  return stored[TAKEOVER] !== false;
}

// ---------------------------------------------------------------------------
// Talking to SDM
// ---------------------------------------------------------------------------

async function handOver(url, referrer, headers) {
  const message = {
    type: "download",
    url: url,
    fileName: fileNameFrom(url),
    referrer: referrer,

    // The real request, when there was one to watch. Everything below is the fallback for
    // a right-clicked link the browser was never asked to fetch.
    headers: headers,

    // What SDM cannot work out for itself. Fetching from another process makes it a
    // different visitor, and a file behind a login is not a file at a URL — it is a file
    // at that URL for whoever is signed in.
    cookie: await cookieHeaderFor(url),
    userAgent: navigator.userAgent,
  };

  return await new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      // lastError must be read inside the callback or Chrome logs it as unchecked.
      if (chrome.runtime.lastError) {
        // Chrome could not start the host at all. Nearly always one of three things: the
        // host is not registered, the path in its manifest is wrong, or this extension's
        // id is missing from allowed_origins.
        resolve({ ok: false, message: chrome.runtime.lastError.message });
        return;
      }

      if (!reply || reply.type !== "accepted") {
        resolve({ ok: false, message: (reply && reply.message) || "SDM did not answer." });
        return;
      }

      // Nothing is shown on success on purpose: SDM's own window is the place a transfer
      // appears, and a notification for every download would be noise the moment this is
      // used in earnest.
      resolve({ ok: true, message: "" });
    });
  });
}

// The Cookie header the browser would have sent. httpOnly cookies are included — they are
// usually the session itself, and they are exactly what a bare request lacks.
async function cookieHeaderFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url: url });

    return cookies.length
      ? cookies.map((cookie) => cookie.name + "=" + cookie.value).join("; ")
      : undefined;
  } catch (error) {
    // No cookie access for this URL. The download may still work; it is not worth
    // abandoning over.
    return undefined;
  }
}

// The name the browser would have used, so SDM can show something meaningful before the
// server has said anything. It is only a suggestion — Content-Disposition wins later.
function fileNameFrom(url) {
  try {
    const path = new URL(url).pathname;
    const last = path.substring(path.lastIndexOf("/") + 1);
    return last ? decodeURIComponent(last) : undefined;
  } catch (error) {
    // A URL the browser accepted but URL() will not parse. SDM validates it again anyway.
    return undefined;
  }
}

function notify(title, message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: "icons/icon48.png",
    title: title,
    message: message,
  });
}
