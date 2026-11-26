// Raindrop server dashboard. No framework and no innerHTML: every value the server sends (usernames
// included) is set as text, never parsed as markup.
"use strict";

const POLL_MS = 5000;
const app = document.getElementById("app");

let pollTimer = null;
let history = [];
let lastStatus = null;
let lastOk = 0;
let recoveryState = { query: "", found: null, issued: null, error: "", pending: [] };
let rolesState = freshRolesState();

// ── DOM helpers ───────────────────────────────────────────────────────────────

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === "class") el.className = value;
    else if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
    else if (key === "text") el.textContent = value;
    // Through the CSSOM: the content security policy refuses style attributes, not style properties.
    else if (key === "style") el.style.cssText = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const SVG = "http://www.w3.org/2000/svg";
function s(tag, attrs) {
  const el = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs || {})) el.setAttribute(key, value);
  return el;
}

// Like replaceChildren, but leaves out null and false, which replaceChildren would print as text.
function fill(el, ...nodes) {
  el.replaceChildren(...nodes.flat(Infinity).filter((node) => node !== null && node !== undefined && node !== false));
}

function show(...nodes) {
  fill(app, ...nodes);
}

// ── Formatting ────────────────────────────────────────────────────────────────

function bytes(n) {
  if (n === null || n === undefined) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`;
}

function percent(n) {
  return n === null || n === undefined ? "—" : `${n < 10 ? n.toFixed(1) : Math.round(n)}%`;
}

function count(n) {
  return n === null || n === undefined ? "—" : Number(n).toLocaleString();
}

function duration(seconds) {
  if (seconds === null || seconds === undefined) return "—";
  const s = Math.floor(seconds);
  const d = Math.floor(s / 86400), hh = Math.floor((s % 86400) / 3600), mm = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${hh}h`;
  if (hh > 0) return `${hh}h ${mm}m`;
  if (mm > 0) return `${mm}m ${s % 60}s`;
  return `${s}s`;
}

function when(iso) {
  if (!iso) return "—";
  const date = new Date(iso);
  return date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

function clock(iso) {
  return new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

// ── API ───────────────────────────────────────────────────────────────────────

class ApiError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 204) return null;

  let data = null;
  try { data = await response.json(); } catch { /* not JSON */ }

  if (!response.ok) {
    throw new ApiError(response.status, data?.code ?? "error",
      data?.message ?? `The server answered ${response.status}.`);
  }
  return data;
}

// ── Start ─────────────────────────────────────────────────────────────────────

async function start() {
  stopPolling();
  let state;
  try {
    state = await api("GET", "/api/state");
  } catch (error) {
    show(h("p", { class: "loading", text: `The dashboard could not reach the server: ${error.message}` }));
    setTimeout(start, POLL_MS);
    return;
  }

  if (!state.setUp) showSetup();
  else if (!state.signedIn) showSignIn();
  else showDashboard(state.username);
}

// ── Setup and sign-in ─────────────────────────────────────────────────────────

function gate(title, intro, fields, submitLabel, onSubmit) {
  const error = h("p", { class: "error", role: "alert" });
  const submit = h("button", { class: "primary", type: "submit", text: submitLabel });
  const form = h("form", {
    onsubmit: async (event) => {
      event.preventDefault();
      error.textContent = "";
      submit.disabled = true;
      try {
        await onSubmit(new FormData(form));
      } catch (e) {
        error.textContent = e.message;
        submit.disabled = false;
      }
    },
  }, fields, submit, error);

  show(h("section", { class: "gate" }, h("h1", { text: title }), intro, form));
  form.querySelector("input")?.focus();
}

function field(label, name, type, extra) {
  const id = `f-${name}`;
  return [h("label", { for: id, text: label }), h("input", { id, name, type, required: true, ...extra })];
}

function showSetup() {
  gate("Set up the dashboard",
    [
      h("p", { text: "Choose the username and password you will sign in to this dashboard with. They are not a Raindrop account." }),
      h("p", { text: "The setup code is printed in the server's console when it starts (in Docker: docker compose logs server)." }),
    ],
    [
      field("Setup code", "code", "text", { autocomplete: "one-time-code", placeholder: "XXXX-XXXX-XXXX", spellcheck: "false" }),
      field("Username", "username", "text", { autocomplete: "username", maxlength: "64" }),
      field("Password (at least 12 characters)", "password", "password", { autocomplete: "new-password", minlength: "12" }),
      field("Password again", "confirm", "password", { autocomplete: "new-password", minlength: "12" }),
    ],
    "Create account",
    async (data) => {
      if (data.get("password") !== data.get("confirm")) throw new Error("The passwords do not match.");
      const result = await api("POST", "/api/setup", {
        code: data.get("code"), username: data.get("username"), password: data.get("password"),
      });
      showDashboard(result.username);
    });
}

function showSignIn(message) {
  gate("Raindrop server",
    h("p", { text: message ?? "Sign in to the owner's dashboard." }),
    [
      field("Username", "username", "text", { autocomplete: "username" }),
      field("Password", "password", "password", { autocomplete: "current-password" }),
    ],
    "Sign in",
    async (data) => {
      const result = await api("POST", "/api/login", { username: data.get("username"), password: data.get("password") });
      showDashboard(result.username);
    });
}

// ── Dashboard ─────────────────────────────────────────────────────────────────

const view = {};

function showDashboard(username) {
  history = [];
  lastStatus = null;
  recoveryState = { query: "", found: null, issued: null, error: "", pending: [] };
  rolesState = freshRolesState();

  view.status = h("span", { class: "status" }, h("span", { class: "dot", "aria-hidden": "true" }), h("span", { text: "Connecting" }));
  view.version = h("span", { class: "muted" });
  view.banner = h("div");
  view.server = h("section", { class: "card" });
  view.process = h("section", { class: "card" });
  view.environment = h("section", { class: "card" });
  view.clients = h("section", { class: "card" });
  view.accounts = h("section", { class: "card" });
  view.storage = h("section", { class: "card" });
  view.recovery = h("section", { class: "card wide" });
  view.roles = h("section", { class: "card wide" });
  view.settings = h("section", { class: "card wide" });

  const header = h("header", { class: "header" },
    h("div", { class: "title" }, h("h1", { text: "Raindrop server" }), view.status, view.version),
    h("div", { class: "spacer" }),
    h("span", { class: "who", text: `Signed in as ${username}` }),
    h("button", { type: "button", text: "Sign out", onclick: signOut }));

  show(header, view.banner,
    h("div", { class: "grid" }, view.process, view.environment, view.clients, view.accounts, view.server, view.storage),
    h("h2", { class: "section-title", text: "Members" }),
    h("div", { class: "grid" }, view.roles, view.recovery),
    h("h2", { class: "section-title", text: "This dashboard" }),
    h("div", { class: "grid" }, view.settings));

  renderRecovery();
  renderRoles();
  renderSettings();
  poll(true);
  loadPending();
  loadRoles();
}

async function signOut() {
  stopPolling();
  try { await api("POST", "/api/logout"); } catch { /* signed out either way */ }
  showSignIn("Signed out.");
}

function stopPolling() {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = null;
}

async function poll(first) {
  stopPolling();
  try {
    const since = history.length ? `?since=${encodeURIComponent(history[history.length - 1].atUtc)}` : "";
    const [status, recent] = await Promise.all([api("GET", "/api/status"), api("GET", `/api/history${since}`)]);
    history = history.concat(recent.samples).slice(-720);
    lastStatus = status;
    lastOk = Date.now();
    render(status);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      showSignIn("Your session ended. Sign in again.");
      return;
    }
    markStale(error);
  }
  if (first || app.contains(view.status)) pollTimer = setTimeout(() => poll(false), POLL_MS);
}

function markStale(error) {
  const stale = Date.now() - lastOk > POLL_MS * 3;
  view.status.className = `status ${stale ? "down" : "stale"}`;
  view.status.lastChild.textContent = stale ? "Not answering" : "Reconnecting";
  view.status.title = error.message;
}

function render(st) {
  view.status.className = "status";
  view.status.lastChild.textContent = "Running";
  view.status.title = `Last updated ${clock(st.generatedAtUtc)}`;
  view.version.textContent = `v${st.server.version.split("+")[0]} · up ${duration(st.server.uptimeSeconds)}`;

  renderBanner(st.environment);
  renderProcess(st.process);
  renderEnvironment(st.environment);
  renderClients(st.clients);
  renderAccounts(st.accounts);
  renderServer(st.server);
  renderStorage(st.storage);
}

// ── Cards ─────────────────────────────────────────────────────────────────────

function card(el, title, hint, ...body) {
  fill(el, h("h2", {}, h("span", { text: title }), hint ? h("span", { class: "hint", text: hint }) : null), ...body);
}

function tile(value, label, sub) {
  return h("div", { class: "tile" },
    h("div", { class: "value", text: value }),
    h("div", { class: "label", text: label }),
    sub ? h("div", { class: "sub", text: sub }) : null);
}

function facts(rows) {
  return h("dl", { class: "facts" }, rows.filter(Boolean).flatMap(([k, v, cls]) =>
    [h("dt", { text: k }), h("dd", { class: cls ?? null, text: v ?? "—" })]));
}

function meter(used, total, label) {
  if (used === null || used === undefined || !total) return null;
  const share = Math.max(0, Math.min(1, used / total));
  const high = share >= 0.9;
  return h("div", {},
    h("div", { class: `meter${high ? " high" : ""}`, role: "meter", "aria-valuemin": "0", "aria-valuemax": String(total),
      "aria-valuenow": String(used), "aria-label": label },
      h("span", { style: `width:${(share * 100).toFixed(1)}%` })),
    high ? h("span", { class: "flag", text: "▲ Over 90%" }) : null);
}

function renderBanner(env) {
  if (env.kind !== "container") {
    fill(view.banner);
    return;
  }
  const docker = env.runtime === "docker";
  fill(view.banner, h("div", { class: "banner", role: "note" },
    h("span", { class: "icon", "aria-hidden": "true", text: "▣" }),
    h("div", {},
      h("strong", { text: docker ? "Running inside Docker" : "Running inside a container" }),
      h("span", { class: "secondary", text: "Figures below are the container's own — its limits and usage — not the machine it runs on." }))));
}

function renderProcess(p) {
  card(view.process, "Server process", "last hour",
    metric("CPU", percent(p.cpuPercent), "of all cores", "processCpuPercent", percent),
    metric("Memory", bytes(p.workingSetBytes), `heap ${bytes(p.managedHeapBytes)}`, "processMemoryBytes", bytes),
    h("div", { class: "metric" }, facts([
      ["Threads", count(p.threads)],
      ["Garbage collections", count(p.garbageCollections)],
    ])));
}

function renderEnvironment(env) {
  if (env.kind === "container") return renderContainer(env.container, env.runtime);
  const m = env.host;
  const used = m.memoryTotalBytes !== null && m.memoryAvailableBytes !== null ? m.memoryTotalBytes - m.memoryAvailableBytes : null;

  card(view.environment, "Machine", m.machineName,
    metric("CPU", percent(m.cpuPercent), `${m.cpuCount} core${m.cpuCount === 1 ? "" : "s"}`, "systemCpuPercent", percent),
    h("div", { class: "metric" },
      h("div", { class: "head" },
        h("span", {}, h("span", { class: "now", text: bytes(used) }), " ", h("span", { class: "of", text: `of ${bytes(m.memoryTotalBytes)} memory in use` }))),
      meter(used, m.memoryTotalBytes, "Memory in use")),
    h("div", { class: "metric" }, facts([
      ["System", m.operatingSystem],
      m.operatingSystem !== m.kernel ? ["Kernel", m.kernel] : null,
      ["Architecture", m.architecture],
      ["Processor", m.cpuModel],
      m.loadAverage ? ["Load (1, 5, 15 min)", m.loadAverage.map((v) => v.toFixed(2)).join("  ")] : null,
      ["Up for", duration(m.uptimeSeconds)],
    ])));
}

function renderContainer(c, runtime) {
  const cpuOf = c.cpuLimitCores ? `of ${c.cpuLimitCores.toFixed(c.cpuLimitCores % 1 ? 1 : 0)} core limit` : "no CPU limit";
  card(view.environment, runtime === "docker" ? "Docker container" : "Container", c.containerId ? c.containerId.slice(0, 12) : c.hostname,
    metric("CPU", percent(c.cpuPercent), c.cpuCoresUsed !== null ? `${c.cpuCoresUsed.toFixed(2)} cores, ${cpuOf}` : cpuOf,
      "systemCpuPercent", percent),
    h("div", { class: "metric" },
      h("div", { class: "head" },
        h("span", {}, h("span", { class: "now", text: bytes(c.memoryUsedBytes) }), " ",
          h("span", { class: "of", text: c.memoryLimitBytes ? `of ${bytes(c.memoryLimitBytes)} limit` : "memory in use, no limit" }))),
      meter(c.memoryUsedBytes, c.memoryLimitBytes, "Container memory against its limit")),
    h("div", { class: "metric" }, facts([
      ["Container id", c.containerId ?? "—", "mono"],
      ["Hostname", c.hostname],
      ["Image", c.imageOperatingSystem],
      ["Running for", duration(c.uptimeSeconds)],
      ["Processes", c.processLimit ? `${count(c.processes)} of ${count(c.processLimit)}` : count(c.processes)],
      ["Network", c.networkReceivedBytes !== null ? `${bytes(c.networkReceivedBytes)} in · ${bytes(c.networkSentBytes)} out` : null],
    ])));
}

function renderClients(c) {
  card(view.clients, "Connected clients", null,
    h("div", { class: "tiles" },
      tile(count(c.eventStreams), "Clients connected", "open event streams"),
      tile(count(c.membersOnline), "Members online"),
      tile(count(c.sessions), "Signed-in sessions"),
      tile(count(c.voiceParticipants), "In voice calls", `${count(c.voiceCalls)} call${c.voiceCalls === 1 ? "" : "s"}`)),
    h("div", { class: "metric" },
      h("div", { class: "head" }, h("h3", { text: "Clients connected, last hour" })),
      sparkline("eventStreams", count)));
}

function renderAccounts(a) {
  card(view.accounts, "Accounts", null,
    h("div", { class: "tiles" },
      tile(count(a.active), "Registered", "active members"),
      tile(count(a.leftDataPreserved), "Left", "data kept for now"),
      tile(count(a.erased), "Erased", "nothing kept but the id")));
}

function renderServer(sv) {
  card(view.server, "Server", null, facts([
    ["Version", sv.version],
    ["Protocol", sv.protocolVersion],
    ["Started", when(sv.startedAtUtc)],
    ["Runtime", sv.dotnetRuntime],
    ["Database", sv.database],
    ["Voice", sv.voice === "none" ? "off" : sv.voice],
    ["Attachments", sv.attachmentsEnabled ? "on" : "off"],
    ["TLS fingerprint", sv.tlsFingerprint, "mono"],
    ["Identity", sv.identityFingerprint, "mono"],
  ]));
}

function renderStorage(st) {
  const rows = st.volumes.map((v) => {
    const used = v.totalBytes - v.freeBytes;
    return h("div", { class: "metric" },
      h("div", { class: "head" },
        h("span", { class: "secondary", text: `${v.purpose} · ${v.mount}` }),
        h("span", { class: "of", text: `${bytes(v.freeBytes)} free of ${bytes(v.totalBytes)}` })),
      meter(used, v.totalBytes, `Space used on ${v.mount}`));
  });

  card(view.storage, "Storage", null,
    h("div", { class: "tiles" },
      tile(st.attachmentsEnabled ? count(st.attachedFiles) : "off", "Encrypted files", st.attachmentsEnabled ? bytes(st.attachedBytes) : null),
      tile(count(st.pendingUploads), "Uploads not yet sent", bytes(st.pendingBytes))),
    rows);
}

// ── Sparklines: one series each, its own scale, crosshair and tooltip on hover ─

function metric(title, now, of, key, format) {
  return h("div", { class: "metric" },
    h("div", { class: "head" },
      h("span", {}, h("span", { class: "now", text: now }), " ", h("span", { class: "of", text: of })),
      h("h3", { text: title })),
    sparkline(key, format));
}

function sparkline(key, format) {
  const points = history.filter((p) => p[key] !== null && p[key] !== undefined);
  const wrap = h("div", { class: "spark" });

  if (points.length < 2) {
    wrap.append(h("div", { class: "empty", text: "Collecting — the chart fills in every 5 seconds." }));
    return wrap;
  }

  const W = 300, H = 56;
  const t0 = new Date(points[0].atUtc).getTime();
  const t1 = new Date(points[points.length - 1].atUtc).getTime();
  const max = Math.max(...points.map((p) => p[key]), key.endsWith("Percent") ? 10 : 1);
  const x = (p) => ((new Date(p.atUtc).getTime() - t0) / Math.max(1, t1 - t0)) * W;
  const y = (p) => H - (p[key] / max) * (H - 4);

  const line = points.map((p, i) => `${i ? "L" : "M"}${x(p).toFixed(1)},${y(p).toFixed(1)}`).join("");
  const svg = s("svg", { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: "none", role: "img",
    "aria-label": `Last hour: lowest ${format(Math.min(...points.map((p) => p[key])))}, highest ${format(max)}, now ${format(points[points.length - 1][key])}` });
  svg.append(
    s("path", { class: "area", d: `${line}L${W},${H}L0,${H}Z` }),
    s("line", { class: "base", x1: 0, y1: H, x2: W, y2: H }),
    s("path", { class: "line", d: line }));

  const cross = s("line", { class: "cross", y1: 0, y2: H, visibility: "hidden" });
  svg.append(cross);
  const tip = h("div", { class: "tooltip", hidden: true });

  svg.addEventListener("pointermove", (event) => {
    const box = svg.getBoundingClientRect();
    const fraction = Math.max(0, Math.min(1, (event.clientX - box.left) / box.width));
    const target = t0 + fraction * (t1 - t0);
    let best = points[0];
    for (const p of points) if (Math.abs(new Date(p.atUtc).getTime() - target) < Math.abs(new Date(best.atUtc).getTime() - target)) best = p;
    const px = x(best);
    cross.setAttribute("x1", px);
    cross.setAttribute("x2", px);
    cross.setAttribute("visibility", "visible");
    fill(tip, h("span", { class: "t", text: `${clock(best.atUtc)}  ` }), h("strong", { text: format(best[key]) }));
    tip.style.left = `${(px / W) * 100}%`;
    tip.hidden = false;
  });
  svg.addEventListener("pointerleave", () => {
    cross.setAttribute("visibility", "hidden");
    tip.hidden = true;
  });

  wrap.append(svg, tip, h("div", { class: "axis" },
    h("span", { text: clock(points[0].atUtc) }),
    h("span", { text: `peak ${format(Math.max(...points.map((p) => p[key])))}` }),
    h("span", { text: "now" })));
  return wrap;
}

// ── Account recovery ──────────────────────────────────────────────────────────

async function loadPending() {
  try {
    recoveryState.pending = await api("GET", "/api/recoveries");
  } catch { /* shown as empty */ }
  renderRecovery();
}

function renderRecovery() {
  const r = recoveryState;
  const input = h("input", { id: "recover-query", type: "search", placeholder: "Username or account id", value: r.query,
    autocomplete: "off", spellcheck: "false" });

  const form = h("form", {
    class: "row",
    onsubmit: async (event) => {
      event.preventDefault();
      r.query = input.value.trim();
      r.found = null;
      r.issued = null;
      r.error = "";
      try {
        r.found = await api("GET", `/api/accounts/lookup?q=${encodeURIComponent(r.query)}`);
      } catch (error) {
        r.error = error.message;
      }
      renderRecovery();
    },
  }, input, h("button", { type: "submit", text: "Find" }));

  fill(view.recovery,
    h("h2", {}, h("span", { text: "Recover an account" }), h("span", { class: "hint", text: "for a member who lost every device and their recovery words" })),
    h("p", { class: "secondary", text: "Ask the member for their username or their account id. Make sure it is really them — in person, or over a channel you trust — before you issue a code." }),
    h("label", { for: "recover-query", text: "Member" }),
    form,
    r.error ? h("p", { class: "error", role: "alert", text: r.error }) : null,
    r.found ? foundAccount(r.found) : null,
    h("h3", { class: "section-title", text: "Codes waiting to be used" }),
    pendingTable(r.pending));
}

function foundAccount(a) {
  const issued = recoveryState.issued && recoveryState.issued.userId === a.userId ? recoveryState.issued : null;
  const confirm = h("div", { hidden: true });

  const issue = h("button", { class: "primary", type: "button",
    text: a.recovery ? "Issue a new code (replaces the waiting one)" : "Issue a recovery code",
    onclick: () => { issue.hidden = true; confirm.hidden = false; } });

  confirm.append(
    h("div", { class: "warning-box", role: "alert" },
      h("strong", { text: `⚠ Whoever enters this code becomes ${a.username}.` }),
      h("div", { text: "Their current devices are signed out, every member sees their safety number change, and messages they could read before stay unreadable to the new identity." })),
    h("div", { class: "row", style: "margin-top:10px" },
      h("button", { class: "primary", type: "button", text: `Yes, issue a code for ${a.username}`, onclick: async (event) => {
        event.target.disabled = true;
        try {
          recoveryState.issued = await api("POST", `/api/accounts/${a.userId}/recovery`);
          recoveryState.found = await api("GET", `/api/accounts/lookup?q=${encodeURIComponent(a.userId)}`);
        } catch (error) {
          recoveryState.error = error.message;
        }
        await loadPending();
      } }),
      h("button", { type: "button", text: "Cancel", onclick: () => { confirm.hidden = true; issue.hidden = false; } })));

  return h("div", { class: "found" },
    h("h3", { text: a.username }),
    facts([
      ["Account id", a.userId, "mono"],
      ["State", a.state === "active" ? "Active member" : "Left, data kept"],
      ["Joined", when(a.joinedAtUtc)],
      ["Last signed in", when(a.lastSignInAtUtc)],
      a.leftAtUtc ? ["Left", when(a.leftAtUtc)] : null,
      ["Devices", a.devices === null ? "—" : count(a.devices)],
      ["Online now", a.online ? "Yes" : "No"],
      ["Code waiting", a.recovery ? `Yes, until ${when(a.recovery.expiresAtUtc)}` : "No"],
    ]),
    a.roles.length ? h("div", { class: "chips", style: "margin-top:10px" }, a.roles.map((name) => {
      const role = rolesState.roles.find((r) => r.role.name === name);
      return role
        ? h("button", { class: "chip", type: "button", text: name, title: "Open this role", onclick: () => openRole(role.role.roleId) })
        : h("span", { class: "chip", text: name });
    })) : null,
    issued ? issuedCode(issued) : h("div", { style: "margin-top:14px" }, issue, confirm));
}

function issuedCode(issued) {
  const copy = h("button", { type: "button", text: "Copy", onclick: async () => {
    try {
      await navigator.clipboard.writeText(issued.code);
      copy.textContent = "Copied";
    } catch {
      copy.textContent = "Select and copy it";
    }
  } });

  return h("div", {},
    h("div", { class: "code" }, h("span", { class: "mono", text: issued.code }), copy),
    h("p", { class: "secondary", text: `Give this to ${issued.username}. It works once, until ${when(issued.expiresAtUtc)}. They enter it in Raindrop when joining this server from their new identity. It is shown only now; issuing another replaces it, and restarting the server forgets it.` }));
}

function pendingTable(pending) {
  if (!pending.length) return h("p", { class: "muted", text: "None." });

  return h("table", {},
    h("thead", {}, h("tr", {}, h("th", { text: "Member" }), h("th", { text: "Issued" }), h("th", { text: "Works until" }), h("th", {}))),
    h("tbody", {}, pending.map((p) => h("tr", {},
      h("td", { text: p.username }),
      h("td", { text: when(p.issuedAtUtc) }),
      h("td", { text: when(p.expiresAtUtc) }),
      h("td", { class: "num" }, h("button", { class: "danger", type: "button", text: "Cancel code", onclick: async (event) => {
        event.target.disabled = true;
        try {
          await api("DELETE", `/api/recoveries/${p.userId}`);
          if (recoveryState.issued?.userId === p.userId) recoveryState.issued = null;
          if (recoveryState.found?.userId === p.userId) recoveryState.found.recovery = null;
        } catch (error) {
          recoveryState.error = error.message;
        }
        await loadPending();
      } }))))));
}

// ── Roles ─────────────────────────────────────────────────────────────────────
// A role is a named set of permissions; a member may do the union of every role they hold. The
// permissions listed are the ones this server's build knows (GET /api/permissions), so an older server
// simply offers fewer. Nothing here is checked against a permission — whoever is signed in owns the
// server — but the server still protects the owner and everyone roles and the last owner.

const CATEGORY_TITLES = {
  general: "General",
  text: "Text",
  voice: "Voice",
  channels: "Channels",
  moderation: "Moderation",
  administration: "Administration",
};

function freshRolesState() {
  // editing: null, or { roleId (null while creating), kind, name, permissions: Set, members, memberQuery }
  return { roles: [], permissions: [], editing: null, error: "", notice: "" };
}

async function loadRoles() {
  try {
    const [roles, permissions] = await Promise.all([api("GET", "/api/roles"), api("GET", "/api/permissions")]);
    rolesState.roles = roles;
    rolesState.permissions = permissions;
  } catch (error) {
    rolesState.error = error.message;
  }
  renderRoles();
}

async function openRole(roleId) {
  rolesState.error = "";
  rolesState.notice = "";
  try {
    const detail = await api("GET", `/api/roles/${roleId}`);
    rolesState.editing = {
      roleId: detail.role.roleId,
      kind: detail.role.kind,
      name: detail.role.name,
      permissions: new Set(detail.role.permissions),
      members: detail.members,
      memberQuery: "",
    };
  } catch (error) {
    rolesState.error = error.message;
  }
  renderRoles();
  view.roles.scrollIntoView({ block: "start", behavior: "smooth" });
}

function newRole() {
  rolesState.error = "";
  rolesState.notice = "";
  rolesState.editing = { roleId: null, kind: "custom", name: "", permissions: new Set(), members: [], memberQuery: "" };
  renderRoles();
}

function kindLabel(kind) {
  return kind === "owner" ? "Built in · cannot be changed" : kind === "everyone" ? "Built in · every member" : "";
}

function renderRoles() {
  const r = rolesState;
  fill(view.roles,
    h("h2", {}, h("span", { text: "Roles" }), h("span", { class: "hint", text: "members may do everything any of their roles allows" })),
    r.error ? h("p", { class: "error", role: "alert", text: r.error }) : null,
    r.notice ? h("p", { class: "secondary", role: "status", text: r.notice }) : null,
    rolesTable(r.roles),
    h("div", { class: "row", style: "margin-top:12px" }, h("button", { type: "button", text: "New role", onclick: newRole })),
    r.editing ? roleEditor(r.editing) : null);
}

function rolesTable(roles) {
  if (!roles.length) return h("p", { class: "muted", text: "Loading…" });

  return h("table", {},
    h("thead", {}, h("tr", {}, h("th", { text: "Role" }), h("th", { text: "Permissions" }), h("th", { class: "num", text: "Members" }), h("th", {}))),
    h("tbody", {}, roles.map(({ role, members }) => h("tr", {},
      h("td", {}, h("div", { text: role.name }), kindLabel(role.kind) ? h("div", { class: "muted", text: kindLabel(role.kind) }) : null),
      h("td", { class: "secondary", text: role.permissions.includes("administrator")
        ? "Administrator (everything)"
        : role.permissions.length ? `${role.permissions.length} of ${rolesState.permissions.length}` : "None" }),
      h("td", { class: "num", text: role.kind === "everyone" ? "All" : count(members) }),
      h("td", { class: "num" }, h("button", { type: "button", text: role.kind === "owner" ? "View" : "Edit",
        onclick: () => openRole(role.roleId) }))))));
}

function roleEditor(e) {
  const owner = e.kind === "owner";
  const everyone = e.kind === "everyone";
  const creating = e.roleId === null;
  const message = h("p", { class: "error", role: "alert" });

  const name = h("input", { id: "role-name", type: "text", value: e.name, maxlength: "32", autocomplete: "off",
    disabled: owner || everyone, oninput: () => { e.name = name.value; } });

  // Grouped by category, in the order the server lists them; a category this page does not know is "Other".
  const groups = new Map();
  for (const p of rolesState.permissions) {
    const key = CATEGORY_TITLES[p.category] ? p.category : "other";
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(p);
  }

  const checkboxes = [...groups].map(([key, permissions]) => h("fieldset", { class: "perm-group" },
    h("legend", { text: CATEGORY_TITLES[key] ?? "Other" }),
    permissions.map((p) => {
      const box = h("input", { type: "checkbox", id: `perm-${p.name}`, checked: e.permissions.has(p.name), disabled: owner,
        onchange: () => { if (box.checked) e.permissions.add(p.name); else e.permissions.delete(p.name); } });
      return h("label", { class: "perm", for: `perm-${p.name}` }, box,
        h("span", {}, h("strong", { text: p.title ?? p.name }), h("span", { class: "muted", text: ` — ${p.summary}` })));
    })));

  const save = async (event) => {
    event.preventDefault();
    message.textContent = "";
    const permissions = rolesState.permissions.map((p) => p.name).filter((n) => e.permissions.has(n));
    try {
      if (creating) {
        const role = await api("POST", "/api/roles", { name: e.name, permissions });
        rolesState.notice = `Created '${role.name}'.`;
        await loadRoles();
        await openRole(role.roleId);
      } else {
        const body = everyone ? { permissions } : { name: e.name, permissions };
        const role = await api("PATCH", `/api/roles/${e.roleId}`, body);
        rolesState.notice = `Saved '${role.name}'.`;
        await loadRoles();
        await openRole(role.roleId);
      }
    } catch (error) {
      message.textContent = error.message;
    }
  };

  const confirmDelete = h("div", { hidden: true },
    h("div", { class: "warning-box", role: "alert" },
      h("strong", { text: `Delete '${e.name}'?` }),
      h("div", { text: "Everyone holding it loses what it grants. This cannot be undone." })),
    h("div", { class: "row", style: "margin-top:10px" },
      h("button", { class: "danger", type: "button", text: "Yes, delete it", onclick: async () => {
        try {
          const deleted = await api("DELETE", `/api/roles/${e.roleId}`);
          rolesState.editing = null;
          rolesState.notice = deleted.message;
          await loadRoles();
        } catch (error) {
          message.textContent = error.message;
        }
      } }),
      h("button", { type: "button", text: "Cancel", onclick: () => { confirmDelete.hidden = true; } })));

  const form = h("form", { onsubmit: save },
    h("label", { for: "role-name", text: "Name" }), name,
    everyone ? h("p", { class: "muted", text: "Every member holds this role. Its name cannot change; its permissions are what a member can do before anybody gives them a role." }) : null,
    owner ? h("p", { class: "muted", text: "The owner role holds administrator, which is every permission, including ones added later. It cannot be changed or deleted." }) : null,
    h("div", { class: "perm-groups" }, checkboxes),
    owner ? null : h("div", { class: "row", style: "margin-top:14px" },
      h("button", { class: "primary", type: "submit", text: creating ? "Create role" : "Save" }),
      creating || everyone ? null : h("button", { class: "danger", type: "button", text: "Delete role", onclick: () => { confirmDelete.hidden = false; } }),
      h("button", { type: "button", text: "Close", onclick: () => { rolesState.editing = null; renderRoles(); } })),
    message,
    confirmDelete);

  return h("div", { class: "found" },
    h("h3", { text: creating ? "New role" : e.name }),
    form,
    creating || everyone ? null : roleMembers(e),
    owner ? h("div", { class: "row", style: "margin-top:14px" },
      h("button", { type: "button", text: "Close", onclick: () => { rolesState.editing = null; renderRoles(); } })) : null);
}

function roleMembers(e) {
  const message = h("p", { class: "error", role: "alert" });
  const input = h("input", { id: "role-member", type: "search", placeholder: "Username or account id", value: e.memberQuery,
    autocomplete: "off", spellcheck: "false", oninput: () => { e.memberQuery = input.value; } });

  const add = h("form", { class: "row", onsubmit: async (event) => {
    event.preventDefault();
    message.textContent = "";
    try {
      const account = await api("GET", `/api/accounts/lookup?q=${encodeURIComponent(input.value.trim())}`);
      const result = await api("POST", `/api/accounts/${account.userId}/roles/${e.roleId}`);
      rolesState.notice = result.message;
      await loadRoles();
      await openRole(e.roleId);
    } catch (error) {
      message.textContent = error.message;
    }
  } }, input, h("button", { type: "submit", text: "Give role" }));

  const list = e.members.length
    ? h("table", {}, h("tbody", {}, e.members.map((m) => h("tr", {},
      h("td", { text: m.username ?? m.userId }),
      h("td", { class: "num" }, h("button", { class: "danger", type: "button", text: "Remove", onclick: async (event) => {
        event.target.disabled = true;
        try {
          const result = await api("DELETE", `/api/accounts/${m.userId}/roles/${e.roleId}`);
          rolesState.notice = result.message;
          await loadRoles();
          await openRole(e.roleId);
        } catch (error) {
          event.target.disabled = false;
          message.textContent = error.message;
        }
      } }))))))
    : h("p", { class: "muted", text: "Nobody holds this role yet." });

  return h("div", {},
    h("h3", { class: "section-title", text: "Members with this role" }),
    list,
    h("label", { for: "role-member", text: "Give it to" }),
    add,
    message);
}

// ── Settings ──────────────────────────────────────────────────────────────────

function renderSettings() {
  const message = h("p", { class: "error", role: "status" });
  const form = h("form", {
    onsubmit: async (event) => {
      event.preventDefault();
      const data = new FormData(form);
      message.className = "error";
      if (data.get("newPassword") !== data.get("confirm")) {
        message.textContent = "The new passwords do not match.";
        return;
      }
      try {
        await api("POST", "/api/password", { currentPassword: data.get("currentPassword"), newPassword: data.get("newPassword") });
        form.reset();
        message.className = "secondary";
        message.textContent = "Password changed. Other browsers were signed out.";
      } catch (error) {
        message.textContent = error.message;
      }
    },
  },
  h("div", { class: "split" },
    h("div", {}, field("Current password", "currentPassword", "password", { autocomplete: "current-password" })),
    h("div", {}, field("New password (at least 12 characters)", "newPassword", "password", { autocomplete: "new-password", minlength: "12" })),
    h("div", {}, field("New password again", "confirm", "password", { autocomplete: "new-password", minlength: "12" }))),
  h("div", { class: "row", style: "margin-top:14px" }, h("button", { type: "submit", text: "Change password" })),
  message);

  fill(view.settings, 
    h("h2", {}, h("span", { text: "Dashboard password" }), h("span", { class: "hint", text: "forgot it? run dashboard-reset in the server console" })),
    form);
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden && lastStatus && app.contains(view.status)) poll(false);
});

start();
