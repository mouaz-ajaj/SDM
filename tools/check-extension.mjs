// Loads the extension the way Chrome does and checks that it works.
//
// Three separate load failures reached a browser before this existed, and not one of them
// was the kind a build or a test suite can see: an unrecognised manifest key, a listener
// that registered and reported itself as not started, and a variable read before its
// declaration that killed the service worker outright. `node --check` catches none of
// them — it parses the file and stops, and every one of these is a runtime fact.
//
// So this evaluates background.js top to bottom against a stub of the browser API, which
// is exactly what registering a service worker does, and then drives one download through
// it. Run with: node tools/check-extension.mjs
//
// No dependencies, on purpose: a check that needs installing is a check that gets skipped.

import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "extension");
const problems = [];
const notes = [];

function check(condition, failure) {
  if (condition) {
    return true;
  }

  problems.push(failure);
  return false;
}

// ---------------------------------------------------------------------------
// The manifest
// ---------------------------------------------------------------------------

const KNOWN_KEYS = new Set([
  "manifest_version", "name", "version", "version_name", "short_name", "description",
  "key", "minimum_chrome_version", "permissions", "optional_permissions",
  "host_permissions", "optional_host_permissions", "background", "options_ui",
  "options_page", "icons", "action", "content_scripts", "web_accessible_resources",
  "content_security_policy", "default_locale", "author", "homepage_url", "incognito",
  "offline_enabled", "storage", "declarative_net_request", "commands", "omnibox",
  "chrome_url_overrides", "devtools_page", "externally_connectable", "side_panel",
  "sandbox", "update_url",
]);

const manifest = JSON.parse(readFileSync(join(root, "manifest.json"), "utf8"));

for (const key of Object.keys(manifest)) {
  // Chrome reads every key and warns about the ones it does not know. A "_comment" is
  // not a comment to it; it is a warning on the extensions page for as long as it is
  // there. JSON has nowhere to put a sentence — that is what background.js is for.
  check(KNOWN_KEYS.has(key), `manifest.json: Chrome does not recognise the key "${key}"`);
}

for (const [size, path] of Object.entries(manifest.icons ?? {})) {
  check(existsSync(join(root, path)), `manifest.json: icon ${size} is missing (${path})`);
}

const workerPath = manifest.background?.service_worker;
check(workerPath && existsSync(join(root, workerPath)), "manifest.json: the service worker file is missing");
check(
  existsSync(join(root, manifest.options_ui?.page ?? "")),
  "manifest.json: the options page is missing"
);

// ---------------------------------------------------------------------------
// A browser to load it into
// ---------------------------------------------------------------------------

function makeStore() {
  const values = new Map();

  return {
    // null means "everything", which is how the sweep reads the store.
    get: async (keys) => {
      if (keys === null || keys === undefined) {
        return Object.fromEntries(values);
      }

      const wanted = Array.isArray(keys) ? keys : [keys];
      return Object.fromEntries(wanted.filter((k) => values.has(k)).map((k) => [k, values.get(k)]));
    },
    set: async (entries) => {
      for (const [key, value] of Object.entries(entries)) {
        values.set(key, value);
      }
    },
    remove: async (keys) => {
      for (const key of Array.isArray(keys) ? keys : [keys]) {
        values.delete(key);
      }
    },
    values,
  };
}

const listeners = {};
const filters = {};
const sent = [];
const notifications = [];
const downloadCalls = [];

// The filter is kept, not discarded. webRequest listeners are registered with one, and
// which request types it names is the whole question: leaving "image" out is what made
// every attempt at saving a picture report no captured headers, and a stub that ignored
// the filter would have called the listener anyway and said everything was fine.
function slot(name) {
  return {
    addListener: (fn, filter) => {
      listeners[name] = fn;
      filters[name] = filter;
    },
  };
}

/// Delivers a request only if the listener's own filter would have seen it.
function deliver(name, details) {
  const types = filters[name]?.types;

  if (types && !types.includes(details.type)) {
    return false;
  }

  listeners[name](details);
  return true;
}

const session = makeStore();
const local = makeStore();

const chrome = {
  runtime: {
    lastError: undefined,
    onInstalled: slot("onInstalled"),
    onStartup: slot("onStartup"),
    sendNativeMessage: (host, message, callback) => {
      sent.push({ host, message });
      callback({ type: "accepted", message: message.url });
    },
  },
  contextMenus: {
    removeAll: (done) => done(),
    create: () => {},
    onClicked: slot("onMenuClicked"),
  },
  downloads: {
    onCreated: slot("onDownloadCreated"),
    pause: async (id) => downloadCalls.push(["pause", id]),
    resume: async (id) => downloadCalls.push(["resume", id]),
    cancel: async (id) => downloadCalls.push(["cancel", id]),
    removeFile: async (id) => downloadCalls.push(["removeFile", id]),
    erase: async (q) => downloadCalls.push(["erase", q.id]),
  },
  webRequest: { onSendHeaders: slot("onSendHeaders") },
  cookies: { getAll: async () => [{ name: "session", value: "abc123" }] },
  notifications: { create: (options) => notifications.push(options) },
  storage: { session, local },
};

const context = {
  chrome,
  console: { log: () => {} },
  navigator: { userAgent: "Mozilla/5.0 (check)" },
  URL,
  Date,
  Object,
  Array,
  Promise,
  JSON,
  String,
  Number,
  Error,
  setTimeout,
  decodeURIComponent,
};

// ---------------------------------------------------------------------------
// Load it
// ---------------------------------------------------------------------------

try {
  vm.runInNewContext(readFileSync(join(root, workerPath), "utf8"), context, { filename: workerPath });
} catch (error) {
  // This is the one that killed the worker outright: a variable read before its
  // declaration, thrown from inside register(), rethrown from its own catch, and out of
  // the top level. Chrome reported it as "Service worker registration failed".
  problems.push(`${workerPath} threw while being evaluated: ${error}`);
}

for (const required of ["onDownloadCreated", "onMenuClicked", "onSendHeaders"]) {
  check(listeners[required], `${workerPath}: the ${required} listener was never registered`);
}

// ---------------------------------------------------------------------------
// Drive one download through it
// ---------------------------------------------------------------------------

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

if (listeners.onSendHeaders && listeners.onDownloadCreated) {
  const url = "https://example.test/photos/holiday.jpg";

  // A page loading a picture. This is the only request that will ever be made for it:
  // saving it afterwards comes out of the cache, so if this type is not being watched,
  // the download has no captured headers and never will.
  const watched = deliver("onSendHeaders", {
    url,
    method: "GET",
    type: "image",
    requestHeaders: [
      { name: "Cookie", value: "session=abc123" },
      { name: "anthropic-client-version", value: "web_1.2.3" },
    ],
  });

  check(
    watched,
    "requests of type \"image\" are not being watched, so saving a picture can never have "
      + "captured headers — it comes out of the cache and makes no fresh request"
  );

  await settle();

  await listeners.onDownloadCreated({
    id: 7,
    url,
    finalUrl: url,
    state: "in_progress",
    paused: false,
    startTime: new Date().toISOString(),
    referrer: "https://example.test/album",
  });

  await settle();
  await settle();

  const handover = sent.at(-1);

  if (check(handover, "a download was created and nothing was handed to SDM")) {
    check(handover.message.url === url, "the wrong URL was handed over");
    check(
      handover.message.headers?.["anthropic-client-version"] === "web_1.2.3",
      "the real request's headers were not carried over — check the webRequest type filter"
    );
    check(
      downloadCalls.some(([call]) => call === "pause"),
      "the browser's own download was not paused before the handover"
    );
    check(
      downloadCalls.some(([call]) => call === "cancel"),
      "SDM accepted the download and the browser's copy was not cancelled"
    );
  }

  // The diagnostics have to survive three registrations racing each other.
  const status = (await session.get("status")).status ?? {};

  for (const key of ["downloads", "contextMenus", "webRequest"]) {
    check(status[key] === "ok", `the status panel reports "${key}" as ${status[key] ?? "not started"}`);
  }

  notes.push(`handed over ${sent.length} download(s); status reports ${Object.keys(status).length - 1} listeners`);
}

// ---------------------------------------------------------------------------

if (problems.length > 0) {
  console.error("extension check FAILED\n");

  for (const problem of problems) {
    console.error("  - " + problem);
  }

  process.exit(1);
}

console.log("extension check passed");

for (const note of notes) {
  console.log("  " + note);
}
