"use strict";

const TAKEOVER = "takeOverDownloads";
const EXCLUDED = "excludedSites";

const box = document.getElementById("takeover");
const saved = document.getElementById("saved");
const sites = document.getElementById("excluded");
const savedSites = document.getElementById("savedSites");

// Absent means on: the default lives in one place, and background.js reads it the same way.
chrome.storage.local.get([TAKEOVER, EXCLUDED]).then((stored) => {
  box.checked = stored[TAKEOVER] !== false;
  sites.value = (stored[EXCLUDED] || []).join("\n");
});

box.addEventListener("change", async () => {
  await chrome.storage.local.set({ [TAKEOVER]: box.checked });

  // No Save button: a single switch that needs confirming invites the state where the
  // page says one thing and the browser does another.
  saved.textContent = box.checked ? "Downloads go to SDM." : "Downloads stay in Chrome.";
  setTimeout(() => (saved.textContent = ""), 2600);
});

// Saved as you leave the box rather than on every keystroke, so a half-typed hostname is
// never briefly in force.
sites.addEventListener("change", async () => {
  const list = sites.value
    .split("\n")
    .map(clean)
    .filter((entry) => entry.length > 0);

  await chrome.storage.local.set({ [EXCLUDED]: list });

  sites.value = list.join("\n");
  savedSites.textContent = list.length
    ? list.length + (list.length === 1 ? " site kept by the browser." : " sites kept by the browser.")
    : "No sites excluded.";

  setTimeout(() => (savedSites.textContent = ""), 2600);
});

// ---------------------------------------------------------------------------
// Diagnostics
// ---------------------------------------------------------------------------

const WATCHED = [
  ["downloads", "Taking over downloads"],
  ["contextMenus", "Right-click menu"],
  ["webRequest", "Copying the real request headers"],
];

async function show() {
  const stored = await chrome.storage.session.get(["status", "events"]);
  const status = stored.status || {};
  const events = stored.events || [];

  document.getElementById("status").innerHTML = WATCHED.map(([key, label]) => {
    const state = status[key];

    if (state === "ok") {
      return '<div class="ok">on &nbsp; ' + label + "</div>";
    }

    // Undefined is not the same as failed, and saying so matters: it means the worker has
    // not started since the extension was last reloaded, so nothing has been switched on
    // yet at all.
    return state
      ? '<div class="bad">FAILED &nbsp; ' + label + " — " + escapeHtml(state) + "</div>"
      : '<div class="bad">not started &nbsp; ' + label + "</div>";
  }).join("");

  document.getElementById("events").textContent = events.length
    ? events.join("\n")
    : "Nothing yet. Download something and press Refresh.";
}

document.getElementById("refresh").addEventListener("click", show);

// Asks SDM directly, from this page, so a failure here separates "the bridge is broken"
// from "the extension never ran".
document.getElementById("ping").addEventListener("click", () => {
  const out = document.getElementById("pinged");
  out.textContent = "asking…";

  chrome.runtime.sendNativeMessage("com.sdm.host", { type: "ping" }, (reply) => {
    if (chrome.runtime.lastError) {
      out.textContent = "SDM could not be reached: " + chrome.runtime.lastError.message;
      return;
    }

    out.textContent = reply && reply.type === "pong"
      ? "SDM answered. Version " + (reply.version || "unknown") + "."
      : "SDM answered something unexpected: " + JSON.stringify(reply);
  });
});

function escapeHtml(text) {
  return String(text).replace(/[<>&]/g, (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;" })[c]);
}

show();

// People paste whole URLs. Take the hostname out of one rather than storing something that
// will never match anything.
function clean(line) {
  const text = line.trim().toLowerCase();

  if (!text) {
    return "";
  }

  try {
    return new URL(text.includes("://") ? text : "https://" + text).hostname;
  } catch (error) {
    return text;
  }
}
