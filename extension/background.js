// Screen Time Connector
// Reports the active tab's domain (and the sites of all open tabs, for "open time") to the
// desktop app. The desktop app does all the timing
// (it knows when the browser window is actually in the foreground and whether you're idle),
// so this worker only wakes on tab/window events plus a 30s heartbeat, and sends a few bytes.
//
// BROWSER is only a first guess: Vivaldi, Opera, Arc and others report themselves as Chrome. The desktop
// app works out the real browser by matching the tab title we send against the title of the window in
// front, and remembers it per install (the random "instance" id below).
//
// Each install (= each browser installation or profile) is its own "browser" in Screen Time and can be put
// into a tracker group (Work, Personal, ...). Reports say whether this profile's window has focus, so the
// app gives each second to exactly one profile. "t" orders reports in case they arrive out of order.

const ENDPOINT = "http://127.0.0.1:47321/api/tab";
const BROWSER = detectBrowser();

const instanceId = chrome.storage.local.get("instance").then(r => {
  if (r.instance) return r.instance;
  const id = crypto.randomUUID();
  chrome.storage.local.set({ instance: id });
  return id;
});

let lastKey = "";
let lastSentAt = 0;

function detectBrowser() {
  const brands = ((self.navigator.userAgentData && self.navigator.userAgentData.brands) || []).map(b => b.brand);
  if (brands.includes("Microsoft Edge")) return "msedge.exe";
  if (brands.includes("Brave")) return "brave.exe";
  if (brands.includes("Opera") || brands.includes("Opera GX")) return "opera.exe";
  if (brands.includes("Vivaldi")) return "vivaldi.exe";
  if (/Edg\//.test(self.navigator.userAgent)) return "msedge.exe";
  if (/OPR\//.test(self.navigator.userAgent)) return "opera.exe";
  return "chrome.exe";
}

function domainOf(url) {
  if (!url) return null;
  try {
    const u = new URL(url);
    if (u.protocol === "http:" || u.protocol === "https:") return u.hostname.replace(/^www\./, "").toLowerCase() || null;
    if (u.protocol === "file:") return "local files";
    if (/^(chrome|edge|brave|opera|vivaldi):$/.test(u.protocol) && u.hostname) return u.protocol + "//" + u.hostname;
  } catch (_) {}
  return null;
}

function iconOf(tab) {
  const i = tab.favIconUrl;
  if (!i) return undefined;
  if (/^https?:/i.test(i) && i.length <= 2048) return i;
  if (/^data:image\//i.test(i) && i.length <= 12000) return i;
  return undefined;
}

/** Sites of every open tab in this browser profile, without duplicates. */
async function openDomains() {
  const set = new Set();
  try {
    for (const t of await chrome.tabs.query({})) {
      const d = domainOf(t.url || t.pendingUrl);
      if (d) set.add(d);
    }
  } catch (_) {}
  return [...set].sort().slice(0, 300);
}

async function report(force, reason) {
  let tab;
  try {
    [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  } catch (_) { return; }
  const domain = tab ? domainOf(tab.url || tab.pendingUrl) : null;
  const icon = tab && domain ? iconOf(tab) : undefined;
  const open = await openDomains();
  let focused = false;
  try {
    const win = await chrome.windows.getLastFocused();
    focused = !!(win && win.focused);
  } catch (_) {}
  const key = domain + "|" + (icon || "") + "|" + open.join(",") + "|" + focused;
  const now = Date.now();
  if (!force && key === lastKey && now - lastSentAt < 20000) return;
  lastKey = key;
  lastSentAt = now;
  try {
    const res = await fetch(ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-ScreenTime": "1" },
      body: JSON.stringify({
        browser: BROWSER,
        instance: await instanceId,
        domain,
        icon,
        title: tab && tab.title ? tab.title.slice(0, 300) : undefined,
        reason,
        open,
        focused,
        t: Date.now(),
      }),
    });
    maybeReload(res.headers.get("X-ScreenTime-Version"));
  } catch (_) {
    // Desktop app not running; nothing to do.
  }
}

// The desktop app's updates also replace this extension's files in its install folder. A store install
// updates through the store, but an unpacked ("Load unpacked") one only re-reads its files when reloaded,
// so do that once when the app reports a newer version than this one.
const newer = (a, b) => {
  const x = a.split(".").map(Number), y = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0);
  return false;
};
async function maybeReload(appVersion) {
  const mine = chrome.runtime.getManifest().version;
  if (!appVersion || !/^\d+(\.\d+){0,3}$/.test(appVersion) || !newer(appVersion, mine)) return;
  const { reloadedFor } = await chrome.storage.local.get("reloadedFor");
  if (reloadedFor === appVersion) return;   // already tried: this extension's files are older than the app
  const self = await chrome.management.getSelf().catch(() => null);
  if (!self || self.installType !== "development") return;
  await chrome.storage.local.set({ reloadedFor: appVersion });
  chrome.runtime.reload();
}

// "reason" tells the app whether the browser was just brought to the front (focus / tab click),
// which is the only time it uses the title to work out which browser this is.
chrome.tabs.onActivated.addListener(() => report(true, "activated"));
chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  // Active tab: its site or icon changed. Any tab: a new site may have opened (open time).
  if (tab.active ? (change.url || change.favIconUrl || change.status === "complete") : change.url) report(false, "update");
});
chrome.tabs.onCreated.addListener(() => report(false, "tabs"));
chrome.tabs.onRemoved.addListener(() => report(false, "tabs"));
chrome.tabs.onReplaced.addListener(() => report(false, "tabs"));
chrome.windows.onFocusChanged.addListener(id => {
  // WINDOW_ID_NONE: focus left this profile (another app, or another browser profile's window).
  report(true, id === chrome.windows.WINDOW_ID_NONE ? "blur" : "focus");
});
chrome.alarms.onAlarm.addListener(a => { if (a.name === "heartbeat") report(true, "heartbeat"); });
chrome.runtime.onStartup.addListener(() => report(true, "startup"));
chrome.runtime.onInstalled.addListener(() => report(true, "startup"));

chrome.alarms.get("heartbeat", a => {
  if (!a) chrome.alarms.create("heartbeat", { periodInMinutes: 0.5 });
});
