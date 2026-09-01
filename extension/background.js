"use strict";

// The host Chrome launches on our behalf. It has to match the "name" in the host manifest
// and the registry key that tools/install-native-host.ps1 writes; nothing here can work if
// those three disagree, and Chrome's only complaint will be "host not found".
const HOST = "com.sdm.host";
const MENU = "sdm-download";

// A service worker is stopped whenever it is idle, so nothing may live in a variable
// between events. Context menu entries are stored by the browser rather than by us, which
// is why they are created on install and not on every start — creating them again on a
// restart would fail with "duplicate id".
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU,
      title: "Download with SDM",
      contexts: ["link", "image", "video", "audio"],
    });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU) {
    return;
  }

  // A right-click on a linked image reports both. The link is what was aimed at; the
  // image is what happened to be under the pointer.
  const url = info.linkUrl || info.srcUrl;

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
