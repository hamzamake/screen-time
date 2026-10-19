const BASE = "http://127.0.0.1:47321";
const HEADERS = { "Content-Type": "application/json", "X-ScreenTime": "1" };
const $ = id => document.getElementById(id);

function fmt(ms) {
  const m = Math.floor(ms / 60000), h = Math.floor(m / 60);
  return h ? `${h}h ${m % 60}m` : `${m}m`;
}

$("open").onclick = () => chrome.tabs.create({ url: BASE + "/" });

async function instanceId() {
  const r = await chrome.storage.local.get("instance");
  return r.instance;
}

/** This browser's name and tracker group (each browser installation/profile is tracked separately). */
async function loadBrowser() {
  const id = await instanceId();
  const box = $("browserBox");
  if (!id) return;
  const res = await fetch(`${BASE}/api/browser?instance=${encodeURIComponent(id)}`);
  box.hidden = false;
  if (res.status === 404) {
    $("browserNote").textContent = "This browser hasn't reported yet. Open any web page, then open this again.";
    $("browserForm").hidden = true;
    return;
  }
  const b = await res.json();
  $("bName").value = b.name;
  const sel = $("bGroup");
  sel.innerHTML = "";
  sel.append(new Option("Unassigned", "0"));
  for (const g of b.groups) sel.append(new Option(g.name, String(g.id)));
  sel.value = String(b.groupId);
  $("bTotals").textContent = b.groupId
    ? `Today: ${fmt(b.todayMs)} in this browser · ${fmt(b.groupTodayMs)} in ${b.groupName}`
    : `Today: ${fmt(b.todayMs)} in this browser`;
  $("browserNote").textContent = b.groups.length ? "" : "Create groups like Work and Personal in the dashboard (Settings → Browser trackers).";

  const save = async body => {
    const r = await fetch(`${BASE}/api/browser`, { method: "POST", headers: HEADERS, body: JSON.stringify({ instance: id, ...body }) });
    $("browserNote").textContent = r.ok ? "Saved." : await r.text();
    if (r.ok) setTimeout(loadBrowser, 300);
  };
  sel.onchange = () => save({ groupId: +sel.value });
  $("bName").onchange = () => save({ name: $("bName").value });
}

(async () => {
  const dot = $("dot"), status = $("status");
  try {
    const now = await (await fetch(BASE + "/api/now")).json();
    const sum = await (await fetch(`${BASE}/api/summary?from=${now.today}&to=${now.today}`)).json();
    dot.className = "dot ok";
    status.textContent = now.site ? `Connected · ${now.site}` : "Connected to desktop app";
    $("today").textContent = fmt(sum.total);
    const list = $("sites");
    for (const s of sum.sites.slice(0, 5)) {
      const li = document.createElement("li");
      const name = document.createElement("span");
      name.textContent = s.domain;
      const time = document.createElement("b");
      time.textContent = fmt(s.ms);
      li.append(name, time);
      list.appendChild(li);
    }
    $("sitesBox").hidden = sum.sites.length === 0;
    await loadBrowser();
  } catch (_) {
    dot.className = "dot bad";
    status.textContent = "Desktop app isn't running";
  }
})();
