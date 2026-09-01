"use strict";

// The host Chrome launches on our behalf. It has to match the "name" in the host manifest
// and the registry key that tools/install-native-host.ps1 writes; nothing here can work if
// those three disagree, and Chrome's only complaint will be "host not found".
const HOST = "com.sdm.host";

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
    send(url, tab && tab.url);
  }
});

function send(url, referrer) {
  chrome.runtime.sendNativeMessage(
    HOST,
    {
      type: "download",
      url: url,
      fileName: fileNameFrom(url),
      referrer: referrer,
    },
    (reply) => {
      // lastError must be read inside the callback or Chrome logs it as unchecked.
      if (chrome.runtime.lastError) {
        // Chrome could not start the host at all. Nearly always one of three things: the
        // host is not registered, the path in its manifest is wrong, or this extension's
        // id is missing from allowed_origins.
        notify("SDM could not be reached", chrome.runtime.lastError.message);
        return;
      }

      if (!reply || reply.type !== "accepted") {
        notify("SDM refused the download", (reply && reply.message) || "No answer.");
      }

      // Nothing is shown on success on purpose: SDM's own window is the place a transfer
      // appears, and a notification for every link would be noise the moment this is
      // used in earnest.
    }
  );
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
