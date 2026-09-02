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
