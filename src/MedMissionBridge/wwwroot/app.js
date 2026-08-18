const transitions = {
  Received: ["InProgress", "Completed", "Cancelled"],
  InProgress: ["Completed", "Cancelled", "Received"],
  Completed: ["InProgress"],
  Cancelled: ["Received"],
};

// If the bridge stops, the UI must say so instead of silently freezing on
// stale data — every fetch funnels through here so failures surface once.
let offline = false;
async function fetchJson(url, options) {
  try {
    const resp = await fetch(url, options);
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    if (offline) { offline = false; loadHealth(); }
    return await resp.json();
  } catch (e) {
    if (!offline) {
      offline = true;
      document.getElementById("health").textContent = "⚠ bridge unreachable — retrying…";
    }
    throw e;
  }
}

async function loadHealth() {
  const h = await fetchJson("/api/ui/health");
  let line = `HTTP :${h.httpPort} · MWL :${h.mwlPort} (${h.mwlAeTitle}) · mDNS ${h.serviceName} · ${h.dbPath}`;
  if (!h.mwlRunning) line += " ⚠ MWL not running";
  if (!h.mdnsRunning) line += " ⚠ mDNS not running";
  document.getElementById("health").textContent = line;

  // The operator types this into every tablet, so it is shown rather than hidden —
  // this page is reachable from the laptop itself only.
  document.getElementById("apiKeyValue").textContent = h.apiKey;
  document.getElementById("apiKeyNote").textContent = h.apiKeySource === "Generated"
    ? "generated for this laptop on first run — enter it on every tablet"
    : "set in appsettings.json";

  // The per-laptop deployment checks. Warnings first: at a screening site the
  // operator reads the top of the page and nothing else.
  const list = document.getElementById("diagnostics");
  const ordered = (h.diagnostics ?? []).slice().sort((a, b) =>
    (a.severity === "Warning" ? 0 : 1) - (b.severity === "Warning" ? 0 : 1));
  list.replaceChildren(...ordered.map((d) => {
    const li = document.createElement("li");
    li.className = d.severity === "Warning" ? "diag warn" : "diag info";
    li.textContent = `${d.severity === "Warning" ? "⚠" : "ⓘ"} ${d.message}`;
    return li;
  }));
}

async function loadList() {
  const search = document.getElementById("search").value.trim();
  const status = document.getElementById("statusFilter").value;
  const params = new URLSearchParams();
  if (search) params.set("search", search);
  if (status) params.set("status", status);
  let rows;
  try { rows = await fetchJson(`/api/ui/records?${params}`); } catch { return; }
  const tbody = document.querySelector("#records tbody");
  tbody.replaceChildren(...rows.map((r) => {
    const tr = document.createElement("tr");
    tr.dataset.recordId = r.recordId;
    for (const v of [r.no, [r.lastName, r.firstName].filter(Boolean).join(", "),
                     r.city, r.date, r.status, r.receivedAtUtc?.replace("T", " ").slice(0, 19)]) {
      const td = document.createElement("td");
      td.textContent = v ?? "";
      tr.appendChild(td);
    }
    tr.addEventListener("click", () => showDetail(r.recordId));
    return tr;
  }));
}

function renderValue(v) {
  if (Array.isArray(v)) return v.join(", ");
  if (v && typeof v === "object") return "";
  return String(v);
}

async function showDetail(recordId) {
  let detail;
  try { detail = await fetchJson(`/api/ui/records/${encodeURIComponent(recordId)}`); } catch { return; }
  const payload = JSON.parse(detail.rawJson);
  const box = document.getElementById("detail");
  box.replaceChildren();

  const title = document.createElement("h2");
  title.textContent = `${payload.no ?? recordId} — ${detail.status}`;
  box.appendChild(title);

  const actions = document.createElement("div");
  actions.className = "actions";
  for (const to of transitions[detail.status] ?? []) {
    const b = document.createElement("button");
    b.textContent = `→ ${to}`;
    b.addEventListener("click", async () => {
      try {
        const resp = await fetch(`/api/ui/records/${encodeURIComponent(recordId)}/status`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ status: to }),
        });
        if (!resp.ok) alert(`Status change failed (${resp.status})`);
      } catch {
        alert("Status change failed: bridge unreachable");
      }
      await loadList();
      await showDetail(recordId);
    });
    actions.appendChild(b);
  }
  box.appendChild(actions);

  for (const [section, value] of Object.entries(payload)) {
    const h = document.createElement("h3");
    h.textContent = section;
    box.appendChild(h);
    const table = document.createElement("table");
    table.className = "kv";
    const entries = value && typeof value === "object" && !Array.isArray(value)
      ? Object.entries(value) : [[section, value]];
    for (const [k, v] of entries) {
      const tr = document.createElement("tr");
      const kt = document.createElement("td"); kt.textContent = k;
      const vt = document.createElement("td"); vt.textContent = renderValue(v);
      tr.append(kt, vt);
      table.appendChild(tr);
    }
    box.appendChild(table);
  }
}

document.getElementById("copyKey").addEventListener("click", async () => {
  const button = document.getElementById("copyKey");
  const key = document.getElementById("apiKeyValue").textContent;
  // 127.0.0.1 counts as a secure context, so the clipboard API is available; a
  // failure still has to be visible, because a silently empty paste on the tablet
  // is worse than no button at all.
  try {
    await navigator.clipboard.writeText(key);
    button.textContent = "Copied";
  } catch {
    button.textContent = "Copy failed — select it";
  }
  setTimeout(() => { button.textContent = "Copy"; }, 2000);
});

document.getElementById("backup").addEventListener("click", async () => {
  const button = document.getElementById("backup");
  const note = document.getElementById("backupNote");
  button.disabled = true;
  note.textContent = "working…";
  try {
    const result = await fetchJson("/api/ui/backup", { method: "POST" });
    // Show the path: the operator has to find this file to copy it to a USB drive.
    note.textContent = `saved to ${result.path}`;
  } catch {
    note.textContent = "backup failed — check the log window";
  }
  button.disabled = false;
});

document.getElementById("refresh").addEventListener("click", loadList);
document.getElementById("search").addEventListener("input", () => loadList());
document.getElementById("statusFilter").addEventListener("change", loadList);
loadHealth();
loadList();
setInterval(loadList, 5000);
