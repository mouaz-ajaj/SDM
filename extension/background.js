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

// Declared up here, not beside the code that uses them: register() runs at the top of this
// file and writes status through setStatus, and a const declared further down is still in
// its temporal dead zone at that point. The throw would have been swallowed by the very
// try/catch meant to keep diagnostics harmless, leaving the status empty exactly when it
// was needed.
const STATUS = "status";
const EVENTS = "events";

// And this one for exactly the same reason, which I proved by putting it further down and
// watching the whole extension die.
//
// inTurn() below is a function declaration and hoists; the variable it closes over does
// not. register() runs before the declaration is reached, so reading it threw
// ReferenceError inside register's try, the catch called the same code again and threw
// again — this time out of the catch, out of register, and out of the top level. The
// service worker then failed to register at all: no listeners, no menu, no interception,
// and "Status code: 15" on the extensions page.
let pending = Promise.resolve();

// How recently a download must have started before it counts as one beginning now rather
// than one the browser restored from history. Seconds, not minutes: the only thing that has
// to fit inside it is the moment between Chrome creating a download and this listener
// running.
const FRESH_MS = 15_000;

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

// ---------------------------------------------------------------------------
// Listeners, registered first and each on its own
// ---------------------------------------------------------------------------
//
// A service worker registers its listeners by running this file top to bottom. One
// unguarded call that throws — an API the browser did not grant, a permission still
// waiting to be accepted after a reload — ends the script there, and every listener
// below it is silently never registered.
//
// That is exactly what happened. chrome.webRequest was set up above the download
// listener, so when it was unavailable the interception was never installed at all:
// right-click still worked, because it had been registered earlier, while automatic
// downloads went on going to Chrome and no request headers were ever captured. One
// missing permission, two symptoms that looked unrelated, and nothing in the console
// unless you knew to look for the absence.
//
// So each registration now stands alone and says whether it took.

// onDeterminingFilename where it exists, onCreated only where it does not.
//
// The two must never both be live: onCreated fires first and onDeterminingFilename
// second, for the same download, so registering both would hand every file over twice.
register("downloads", () => {
  if (chrome.downloads.onDeterminingFilename) {
    chrome.downloads.onDeterminingFilename.addListener(onDeterminingFilename);
  } else {
    chrome.downloads.onCreated.addListener(onDownloadCreated);
  }
});

register("contextMenus", () => {
  chrome.runtime.onInstalled.addListener(install);
  chrome.runtime.onStartup.addListener(install);
  chrome.contextMenus.onClicked.addListener(onMenuClicked);
});

// Losing this costs the copied headers, and protected downloads with them — but it must
// not cost the interception itself.
register("webRequest", () =>
  chrome.webRequest.onSendHeaders.addListener(
    (details) => {
      if (details.requestHeaders) {
        remember(details.url, details.requestHeaders, details.method);
      }
    },

    // The request types something downloadable can arrive as. A stylesheet, a script, a
    // font or a ping cannot become a download, and watching those meant a write to
    // storage for every one of them, all session, that could never be read back.
    //
    // Everything else stays, and trimming this list twice taught the same lesson twice.
    // xmlhttprequest is what an application's own API answers, with headers nobody
    // outside the site could name. image and media are what "Save image as" and "Save
    // video as" act on — and those are the case that fails hardest, because the picture
    // is usually already in the cache, so saving it makes no fresh request at all: the
    // only capture that will ever exist is the one from when the page loaded it. Leaving
    // image out is why every attempt at saving a picture reported no captured headers.
    //
    // The storage this was meant to bound is bounded by the sweep below, not by this
    // list. Narrowing the list was solving the wrong half.
    {
      urls: ["http://*/*", "https://*/*"],
      types: ["main_frame", "sub_frame", "xmlhttprequest", "image", "media", "object", "other"],
    },

    // extraHeaders is required for Cookie and the Sec-* family: without it Chrome hides
    // exactly the headers that decide whether a protected download is allowed.
    ["requestHeaders", "extraHeaders"]
  )
);

// Every method is recorded, not only GET.
//
// The method used to be a filter here, which threw away the one fact that says a download
// cannot be taken: a file answering a POST cannot be fetched again by asking for it, and
// neither SDM nor a hand-back to Chrome can reproduce it. Discarding those captures left
// a POST download indistinguishable from an image served out of the cache — both simply
// had nothing recorded — so the one that had to be left alone was taken anyway.

function register(what, addListener) {
  try {
    addListener();
    log("listening:", what);
    setStatus(what, "ok");
  } catch (error) {
    // Never rethrown: one unavailable API must not take the others down with it.
    log("NOT LISTENING:", what, "—", String(error));
    setStatus(what, String(error));
  }
}

// ---------------------------------------------------------------------------
// Saying what actually happened
// ---------------------------------------------------------------------------
//
// Two rounds of fixes changed nothing the user could see, and neither of us could tell
// whether the code was even running: a service worker that fails to register a listener
// does so silently, and looks identical to one whose logic is wrong. The options page
// reads what is written here, so the next question is answered by looking rather than by
// another guess.

// Every read-modify-write on session storage queues behind the last one.
//
// Without this the diagnostics lied, and lied in the most misleading way available. The
// three register() calls run one after another with nothing awaited between them, so all
// three read the status object at the same moment — before any of them had written — each
// added its own key to that same empty object, and each wrote the whole thing back. Last
// writer won. Two listeners that had registered perfectly well reported "not started",
// and the activity log lost most of its lines the same way.
//
// A panel built to answer "did this actually run?" was answering it wrongly, which is
// worse than not having one.
//
// `pending` is declared at the top of the file, with the other state register() reaches
// before this point is ever evaluated.
function inTurn(work) {
  const next = pending.then(work, work);

  // The chain itself must never end up rejected, or every later turn is skipped.
  pending = next.then(
    () => undefined,
    () => undefined
  );

  return next;
}

function setStatus(what, state) {
  return inTurn(async () => {
    try {
      const stored = await chrome.storage.session.get(STATUS);
      const status = stored[STATUS] || {};

      status[what] = state;
      status.startedAt = new Date().toISOString();

      await chrome.storage.session.set({ [STATUS]: status });
    } catch (error) {
      // Nothing to do: this is the diagnostics, not the feature.
    }
  });
}

function record(line) {
  return inTurn(async () => {
    try {
      const stored = await chrome.storage.session.get(EVENTS);
      const events = stored[EVENTS] || [];

      events.push(new Date().toLocaleTimeString() + "  " + line);

      await chrome.storage.session.set({ [EVENTS]: events.slice(-60) });
    } catch (error) {
      // Same.
    }
  });
}

function onMenuClicked(info, tab) {
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
}

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
// chrome.storage.session arrives in Chrome 102, which is what minimum_chrome_version in
// the manifest says — and why. It used to say 88: between the two the extension installed
// without complaint and then failed at the first download, with the error swallowed by a
// catch that exists to keep diagnostics harmless.
//
// The note lives here rather than beside the number it explains, because a manifest is
// JSON and JSON has nowhere to put a sentence. A "_comment" key is not a comment; Chrome
// reads the whole file and warns about every key it does not recognise, which is a
// warning on the extensions page for as long as the note is there.
const RECENT_MS = 120_000;

// How often the expired captures are swept out. Comfortably shorter than RECENT_MS, so
// nothing lingers long past the point it could be used.
const SWEEP_EVERY_MS = 30_000;

function keyFor(url) {
  return "req:" + url;
}

// ---------------------------------------------------------------------------
// The loop guard
// ---------------------------------------------------------------------------
//
// A download SDM refuses is handed back by creating it again, which brings it through
// onDeterminingFilename a second time. Without a record of what was handed back, it would
// be taken, refused, handed back and taken again for as long as the browser is open.
//
// Kept in storage.session rather than a variable for the same reason the captured headers
// are: a service worker is stopped when idle, and the round trip through SDM is easily
// long enough for that to happen in the middle of it.
const HANDED_BACK_MS = 60_000;

function handedBackKeyFor(url) {
  return "ret:" + url;
}

async function rememberHandedBack(url) {
  try {
    await chrome.storage.session.set({ [handedBackKeyFor(url)]: { at: Date.now() } });
  } catch (error) {
    // Losing this risks one extra round trip, not a loop: the entry below is consumed on
    // sight, so at worst the download is offered to SDM once more and refused again.
  }
}

/// True once per hand-back. The record is removed as it is read, so a later download of
/// the same URL is a new decision rather than one this guard silently skips.
async function wasHandedBack(url) {
  const key = handedBackKeyFor(url);

  try {
    const stored = await chrome.storage.session.get(key);
    const entry = stored[key];

    if (!entry) {
      return false;
    }

    await chrome.storage.session.remove(key);

    return Date.now() - entry.at < HANDED_BACK_MS;
  } catch (error) {
    return false;
  }
}

async function remember(url, headers, method) {
  const captured = {};

  for (const header of headers) {
    if (typeof header.value === "string") {
      captured[header.name] = header.value;
    }
  }

  try {
    await chrome.storage.session.set({ [keyFor(url)]: { headers: captured, method, at: Date.now() } });
    await forgetExpired();
  } catch (error) {
    // Session storage has a size cap. Losing one capture is not worth failing a download.
  }
}

// Nothing ever removed a capture. RECENT_MS was applied when reading, so a stale entry
// was ignored — and kept, along with every other entry, until the browser closed. Each
// one holds a full header set including the Cookie, so the store grew all session and
// eventually hit the quota, at which point new captures failed silently and protected
// downloads went back to answering 403 for no visible reason.
//
// Swept on write rather than on a timer: a service worker is stopped when idle, so a
// timer is not something this file may rely on, and the only moment the store grows is
// the moment something is added to it.
// Reading the whole store to sweep it is not something to do on every capture. This
// resets whenever the worker is stopped, which costs one extra sweep and nothing else.
let sweptAt = 0;

async function forgetExpired() {
  if (Date.now() - sweptAt < SWEEP_EVERY_MS) {
    return;
  }

  sweptAt = Date.now();

  const everything = await chrome.storage.session.get(null);
  const cutoff = Date.now() - RECENT_MS;
  const stale = [];

  for (const [key, value] of Object.entries(everything)) {
    const captured = key.startsWith("req:") && (!value || typeof value.at !== "number" || value.at < cutoff);

    // Hand-back records are normally consumed on sight; this clears the ones whose
    // download never came back — a browser closed mid-handover, say.
    const handedBack =
      key.startsWith("ret:") && (!value || typeof value.at !== "number" || value.at < Date.now() - HANDED_BACK_MS);

    if (captured || handedBack) {
      stale.push(key);
    }
  }

  if (stale.length) {
    await chrome.storage.session.remove(stale);
    log("forgot", stale.length, "expired header captures");
  }
}

// Tries the final URL and the original, because a download that was redirected reports one
// of each and the request was watched under whichever came first.
//
// Returns the whole capture rather than only its headers: the method is what says whether
// this download can be taken at all, and that decision is made before anything is
// cancelled.
async function capturedFor(...urls) {
  for (const url of urls) {
    if (!url) {
      continue;
    }

    try {
      const stored = await chrome.storage.session.get(keyFor(url));
      const entry = stored[keyFor(url)];

      if (entry && Date.now() - entry.at < RECENT_MS) {
        log(
          "matched the real request:",
          Object.keys(entry.headers).length + " headers,",
          entry.method || "method unrecorded"
        );

        return entry;
      }
    } catch (error) {
      // Fall through to the next candidate.
    }
  }

  log("no captured request for this download; falling back to cookie and referer alone");
  return undefined;
}

/// The headers alone, for the right-click path, where there is no download to decide about.
async function headersFor(...urls) {
  const captured = await capturedFor(...urls);
  return captured && captured.headers;
}

function log(...parts) {
  console.log("[SDM]", ...parts);
  record(parts.join(" "));
}

// ---------------------------------------------------------------------------
// Taking over the browser's own downloads
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// The early hook
// ---------------------------------------------------------------------------
//
// chrome.downloads.onCreated fires after Chrome has decided the response is a download
// and begun settling its name — and settling the name is when Chrome shows "Where do you
// want to save this?". Everything done from there is a reaction to a download that
// already exists and a dialog the user has already been asked. Two prompts for one file.
//
// onDeterminingFilename fires during that settling, before the prompt. Returning true
// promises Chrome that suggest() will be called later; when this takes the download it
// simply never calls it, and cancels instead. The name is never settled, so the dialog
// never appears.
//
// The price is real and worth stating plainly: the download has to be cancelled before
// SDM has agreed to take it, because waiting for an answer — which can mean launching
// SDM and waiting on a pipe — is far longer than the moment available here. So the
// guarantee changes. It used to be "nothing is taken from Chrome until SDM accepts". It
// is now "if SDM refuses, the download is handed straight back", by re-issuing it. A
// re-issued download starts again rather than resuming, which is the cost of not being
// asked twice for every file.
function onDeterminingFilename(item, suggest) {
  if (!canTakeOver(item)) {
    suggest();
    return false;
  }

  // Whether the download still belongs to Chrome.
  //
  // This matters because the two ways of giving one back are opposites, and picking the
  // wrong one loses the file. Before it is cancelled, settling a name hands it back. After
  // it is cancelled, settling a name would name a download that no longer exists, and the
  // only way back is to create it again.
  //
  // The catch below used to do neither. Anything that threw before the cancel — storage
  // unavailable, a permission revoked mid-session — left the name unsettled and the
  // download uncancelled: stuck at "Starting…" for ever, fetched by nobody, and with no
  // entry the user could resume. Under the old hook the same throw left it merely paused,
  // which the user could resume by hand. Cancelling first raises the price of every
  // mistake here, so no path may end without deciding.
  const held = { taken: false };

  intercept(item, suggest, held).catch((error) => {
    log("intercept threw:", String(error));

    if (held.taken) {
      handBack(item.finalUrl || item.url, item).catch(() => {});
      notify("SDM could not take the download", String(error) + " Chrome is downloading it instead.");
    } else {
      suggest();
    }
  });

  return true;
}

async function intercept(item, suggest, held) {
  const url = item.finalUrl || item.url;

  if (!(await enabled()) || (await excluded(url))) {
    log("left to Chrome (turned off, or an excluded site):", url);
    suggest();
    return;
  }

  // The loop guard. A download handed back to Chrome is created by us, so it arrives here
  // again — and without this it would be taken, refused, handed back, and taken again,
  // for as long as the browser is open.
  if (await wasHandedBack(url)) {
    log("this is the copy we just gave back; leaving it alone:", url);
    suggest();
    return;
  }

  const captured = await capturedFor(url, item.url);

  // A file that answers a POST cannot be fetched again by asking for it.
  //
  // SDM would send a GET and get something else — a login page, a 405, an error document
  // — and saving that under the wanted file's name is the failure this whole design
  // exists to prevent. Worse, handing it back cannot rescue it either: downloads.download
  // would issue a GET too. Chrome is the only thing here that can still complete it, so
  // it is left alone before anything is cancelled.
  if (captured && captured.method && captured.method !== "GET") {
    log("left to Chrome: this answers a " + captured.method + ", which cannot be repeated:", url);
    suggest();
    return;
  }

  // Cancelled and erased before the handover. See above: this is the trade this hook asks
  // for, and it is the whole reason the dialog does not appear.
  await chrome.downloads.cancel(item.id).catch(() => {});
  await chrome.downloads.erase({ id: item.id }).catch(() => {});
  held.taken = true;

  log("took it before Chrome could ask:", url);

  const reply = await answerOrGiveUp(
    handOver(url, item.referrer, captured && captured.headers, suggestedNameFrom(item))
  );

  if (!reply.ok) {
    log("SDM refused; handing it back to Chrome:", reply.message);
    await handBack(url, item);
    notify("SDM did not take the download", reply.message + " Chrome is downloading it instead.");
    return;
  }

  log("SDM took it:", url);
}

/// How long SDM has to answer before the download is given back to the browser.
///
/// The host allows itself two seconds to reach a running SDM and twenty to start one, so
/// this is that with room to spare. It exists because the download has already been
/// cancelled by the time we are waiting: a handover that never settles — a native host
/// that hangs rather than exits — would otherwise leave the file fetched by nobody, with
/// nothing on screen to say so. Set too short it would cost a duplicate; set to nothing
/// at all it costs the file.
const HANDOVER_TIMEOUT_MS = 45_000;

function answerOrGiveUp(handover) {
  return Promise.race([
    handover,
    new Promise((resolve) =>
      setTimeout(
        () => resolve({ ok: false, message: "SDM did not answer in time." }),
        HANDOVER_TIMEOUT_MS
      )
    ),
  ]);
}

/// Gives a refused download back to the browser, which starts it again from the beginning.
async function handBack(url, item) {
  // Recorded before the download is created, not after: the new download can reach
  // onDeterminingFilename before this function's next line runs.
  await rememberHandedBack(url);

  // No saveAs. Forcing the dialog here would ask a user who turned that setting off a
  // question they had already answered, on the one path that is meant to be Chrome
  // behaving normally. Whoever wants the prompt has it switched on already.
  const request = { url };
  const name = suggestedNameFrom(item);

  // download() wants a path relative to the downloads folder and rejects an absolute one,
  // while the name Chrome proposes here is usually absolute.
  if (name) {
    request.filename = name;
  }

  try {
    await chrome.downloads.download(request);
  } catch (error) {
    log("could not hand the download back:", String(error));
    notify("The download was lost", "SDM refused it and Chrome would not take it back: " + String(error));
  }
}

/// Chrome's own proposed name, which it took from Content-Disposition — better than a URL.
function suggestedNameFrom(item) {
  const proposed = item.filename || "";
  const name = proposed.split(/[\\/]/).pop();

  return name && name !== "." && name !== ".." ? name : undefined;
}

function onDownloadCreated(item) {
  takeOver(item).catch((error) => {
    log("takeOver threw:", String(error));
    notify("SDM could not take the download", String(error));
  });
}

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

  // Only what SDM can fetch on its own. A blob: or data: URL exists nowhere but inside the
  // page that made it.
  if (!url.startsWith("https://") && !url.startsWith("http://")) {
    log("skipped, cannot be refetched:", url);
    return false;
  }

  // The important one.
  //
  // chrome.downloads.onCreated fires for every download the browser restores from its
  // history when it starts, not only for downloads that are beginning now. A previous
  // version of this function rejected those because it required state "in_progress"; I
  // removed that check to stop fast downloads being left to Chrome, and by doing so handed
  // the entire download history to SDM the next time the browser opened — ten transfers of
  // files fetched weeks ago, all at once.
  //
  // Age is the honest test, because it is the thing that actually distinguishes them. A
  // download beginning now started a moment ago; a restored one started whenever it did.
  // An unparseable time counts as old: flooding SDM is far worse than missing one file.
  const age = Date.now() - Date.parse(item.startTime);

  if (!(age >= 0 && age < FRESH_MS)) {
    log("skipped, restored from history rather than starting now:", url);
    return false;
  }

  // A download the user paused themselves is not one to take over, and neither is one this
  // extension paused and never finished handing over.
  if (item.paused) {
    log("skipped, already paused:", url);
    return false;
  }

  // "complete" is allowed alongside "in_progress" precisely because it may have finished
  // inside the handover window — but only while it is this recent.
  if (item.state !== "in_progress" && item.state !== "complete") {
    log("skipped, state is " + item.state + ":", url);
    return false;
  }

  return true;
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

async function handOver(url, referrer, headers, suggestedName) {
  const message = {
    type: "download",
    url: url,

    // What Chrome proposed, which it read from Content-Disposition, in preference to
    // the last segment of a URL that often ends in an opaque id.
    fileName: suggestedName || fileNameFrom(url),
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
