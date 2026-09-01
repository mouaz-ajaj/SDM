"use strict";

const TAKEOVER = "takeOverDownloads";

const box = document.getElementById("takeover");
const saved = document.getElementById("saved");

// Absent means on: the default lives in one place, and background.js reads it the same way.
chrome.storage.local.get(TAKEOVER).then((stored) => {
  box.checked = stored[TAKEOVER] !== false;
});

box.addEventListener("change", async () => {
  await chrome.storage.local.set({ [TAKEOVER]: box.checked });

  // No Save button: a single switch that needs confirming invites the state where the
  // page says one thing and the browser does another.
  saved.textContent = box.checked
    ? "Downloads go to SDM."
    : "Downloads stay in Chrome.";

  setTimeout(() => (saved.textContent = ""), 2600);
});
