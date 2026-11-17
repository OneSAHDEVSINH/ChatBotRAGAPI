// Admin page: documents, ingestion jobs, readiness and feedback, over the same API as Swagger.
// Plain JS, no dependencies. Server text is only ever inserted with textContent / text nodes, never parsed as HTML.
"use strict";

const KEY_STORAGE = "knowledge-chat-admin-key";
const KB_STORAGE = "knowledge-chat-admin-kb";
const $ = (id) => document.getElementById(id);

/** The knowledge base the sections below work on (top bar select). */
const currentKb = () => $("kb-select").value || "default";
const kbQuery = () => `knowledgeBase=${encodeURIComponent(currentKb())}`;

let apiKey = "";
try { apiKey = sessionStorage.getItem(KEY_STORAGE) || ""; } catch { /* storage unavailable */ }

// ---------- api ----------

class AuthError extends Error {}

/** fetch with the API key; throws a readable Error on failure, AuthError on 401. */
async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (apiKey) headers.set("X-Api-Key", apiKey);
  const response = await fetch(path, { ...options, headers });
  if (response.status === 401) throw new AuthError("The API key is missing or wrong. Enter it above.");
  if (!response.ok) {
    let message = `The request failed (${response.status}).`;
    try {
      const body = await response.json();
      if (typeof body === "string") message = body;
      else if (body?.detail || body?.title) message = body.detail || body.title;
    } catch { /* not JSON */ }
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

function say(id, text, kind = "") {
  const el = $(id);
  el.textContent = text;
  el.dataset.kind = kind;
}

/** Clears a status line that still shows an earlier failure, once its section loads again. */
function clearError(id) {
  if ($(id).dataset.kind === "error") say(id, "");
}

/** The button that submitted a form; a script or some assistive tech submits without one. */
function submitButton(e) {
  return e.submitter ?? e.target.querySelector('button[type="submit"]');
}

function cell(text, className) {
  const td = document.createElement("td");
  td.textContent = text ?? "";
  if (className) td.className = className;
  return td;
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const when = (iso) => (iso ? dateFormat.format(new Date(iso)) : "");

function badge(status) {
  const span = document.createElement("span");
  span.className = `badge badge--${status.toLowerCase().replace(/\s+/g, "-")}`;
  span.textContent = status;
  return span;
}

function handle(err, statusId) {
  say(statusId, err.message || "Something went wrong.", "error");
  if (err instanceof AuthError) $("api-key").focus();
}

// ---------- status ----------

async function loadStatus() {
  const list = $("checks");
  try {
    // Readiness answers 503 with the same JSON when a check fails, so read it either way.
    const response = await fetch("health/ready");
    const report = await response.json();
    list.replaceChildren(...report.checks.map((check) => {
      const li = document.createElement("li");
      li.append(badge(check.status), document.createTextNode(` ${check.name}: ${check.description ?? ""}`));
      return li;
    }));
  } catch {
    const li = document.createElement("li");
    li.textContent = "The readiness check did not answer.";
    list.replaceChildren(li);
  }
}

// ---------- jobs ----------

let jobsTimer = null;

async function loadJobs() {
  try {
    const jobs = await api("api/ingestion/jobs");
    $("jobs").replaceChildren(...jobs.map((job) => {
      const tr = document.createElement("tr");
      const status = document.createElement("td");
      status.append(badge(job.status));
      tr.append(cell(job.source, "wrap"), cell(job.sourceType), status, cell(job.message, "wrap muted"), cell(when(job.updatedAt), "nowrap"));
      return tr;
    }));
    $("jobs-empty").hidden = jobs.length > 0;
    clearError("add-status");

    // Follow running work: refresh every few seconds until nothing is queued or running.
    const active = jobs.some((j) => j.status === "Queued" || j.status === "Running");
    clearTimeout(jobsTimer);
    if (active) jobsTimer = setTimeout(async () => { await loadJobs(); }, 3000);
    else if (jobsTimer) { jobsTimer = null; loadDocuments(); loadSchedules(); }
  } catch (err) {
    handle(err, "add-status");
  }
}

// ---------- documents ----------

async function loadDocuments() {
  try {
    const docs = await api(`api/documents?${kbQuery()}`);
    $("docs").replaceChildren(...docs.map((doc) => {
      const tr = document.createElement("tr");
      const actions = document.createElement("td");
      actions.className = "actions";
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "btn btn--danger btn--small";
      remove.textContent = "Delete";
      remove.setAttribute("aria-label", `Delete ${doc.source}`);
      remove.addEventListener("click", () => deleteDocument(doc.source, remove));
      actions.append(remove);
      tr.append(cell(doc.source, "wrap"), cell(doc.sourceType), cell(String(doc.chunkCount), "num"), cell(when(doc.ingestedAt), "nowrap"), actions);
      return tr;
    }));
    $("docs-empty").hidden = docs.length > 0;
    clearError("docs-status");
  } catch (err) {
    handle(err, "docs-status");
  }
}

async function deleteDocument(source, button) {
  if (!window.confirm(`Delete "${source}" and all its passages? The bot stops answering from it.`)) return;
  button.disabled = true;
  try {
    await api(`api/documents?source=${encodeURIComponent(source)}&${kbQuery()}`, { method: "DELETE" });
    say("docs-status", `Deleted ${source}.`, "ok");
    await loadDocuments();
  } catch (err) {
    button.disabled = false;
    handle(err, "docs-status");
  }
}

// ---------- adding ----------

$("upload-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const files = [...$("files").files];
  if (files.length === 0) return;
  const button = submitButton(e);
  button.disabled = true;
  let queued = 0;
  try {
    for (const file of files) {
      say("add-status", `Uploading ${file.name}…`);
      const form = new FormData();
      form.append("file", file);
      form.append("knowledgeBase", currentKb());
      await api("api/ingestion/upload", { method: "POST", body: form });
      queued++;
    }
    say("add-status", `${queued} file(s) queued. Follow them in Jobs below.`, "ok");
    $("upload-form").reset();
  } catch (err) {
    handle(err, "add-status");
  } finally {
    button.disabled = false;
    loadJobs();
  }
});

$("crawl-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const button = submitButton(e);
  button.disabled = true;
  try {
    const maxPages = Number($("crawl-pages").value);
    await api("api/ingestion/scrape", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url: $("crawl-url").value.trim(), maxPages, removeMissing: maxPages > 1 && $("crawl-remove").checked, knowledgeBase: currentKb() }),
    });
    say("add-status", "Crawl queued. Follow it in Jobs below.", "ok");
  } catch (err) {
    handle(err, "add-status");
  } finally {
    button.disabled = false;
    loadJobs();
  }
});

// ---------- scheduled crawls ----------

const every = (hours) => (hours === 168 ? "week" : hours === 24 ? "day" : `${hours} hours`);

function actionButton(text, label, className, onClick) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `btn ${className} btn--small`;
  button.textContent = text;
  button.setAttribute("aria-label", label);
  button.addEventListener("click", () => onClick(button));
  return button;
}

async function loadSchedules() {
  try {
    const schedules = (await api("api/schedules")).filter((s) => (s.knowledgeBase ?? "default") === currentKb());
    $("schedules").replaceChildren(...schedules.map((s) => {
      const tr = document.createElement("tr");
      const last = document.createElement("td");
      last.className = "wrap";
      if (s.lastJobStatus) last.append(badge(s.lastJobStatus), document.createTextNode(` ${s.lastJobMessage ?? ""}`));
      else last.textContent = s.lastRunAt ? when(s.lastRunAt) : "Not run yet";
      const actions = document.createElement("td");
      actions.className = "actions";
      actions.append(
        actionButton("Run now", `Run the crawl of ${s.url} now`, "btn--card", (b) => runSchedule(s.id, b)),
        actionButton("Delete", `Delete the schedule for ${s.url}`, "btn--danger", (b) => deleteSchedule(s, b)));
      const pages = `${s.maxPages} page${s.maxPages === 1 ? "" : "s"}${s.removeMissing ? ", removes gone pages" : ""}`;
      tr.append(cell(`${s.url} (${pages})`, "wrap"), cell(every(s.intervalHours), "nowrap"), cell(when(s.nextRunAt), "nowrap"), last, actions);
      return tr;
    }));
    $("schedules-empty").hidden = schedules.length > 0;
    clearError("schedules-status");
  } catch (err) {
    handle(err, "schedules-status");
  }
}

$("schedule-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const button = submitButton(e);
  button.disabled = true;
  try {
    const maxPages = Number($("schedule-pages").value);
    await api("api/schedules", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        url: $("schedule-url").value.trim(),
        maxPages,
        removeMissing: maxPages > 1 && $("schedule-remove").checked,
        intervalHours: Number($("schedule-interval").value),
        knowledgeBase: currentKb(),
      }),
    });
    say("schedules-status", "Scheduled. The first crawl starts within a minute.", "ok");
    $("schedule-url").value = "";
    await loadSchedules();
  } catch (err) {
    handle(err, "schedules-status");
  } finally {
    button.disabled = false;
  }
});

async function runSchedule(id, button) {
  button.disabled = true;
  try {
    await api(`api/schedules/${id}/run`, { method: "POST" });
    say("schedules-status", "Crawl started. Follow it in Jobs below.", "ok");
    loadJobs();
  } catch (err) {
    handle(err, "schedules-status");
  } finally {
    button.disabled = false;
    loadSchedules();
  }
}

async function deleteSchedule(schedule, button) {
  if (!window.confirm(`Stop re-crawling ${schedule.url}? Pages already ingested stay.`)) return;
  button.disabled = true;
  try {
    await api(`api/schedules/${schedule.id}`, { method: "DELETE" });
    say("schedules-status", `Stopped re-crawling ${schedule.url}.`, "ok");
    await loadSchedules();
  } catch (err) {
    button.disabled = false;
    handle(err, "schedules-status");
  }
}

// ---------- knowledge bases ----------

function chatLink(id, key) {
  const url = new URL("./", location.href);
  if (id !== "default") url.searchParams.set("kb", id);
  if (key) url.hash = `key=${encodeURIComponent(key)}`;
  return url.href;
}

function showKey(id, key) {
  $("kb-key-id").textContent = id;
  $("kb-key-value").textContent = key;
  $("kb-key-link").textContent = chatLink(id, key);
  $("kb-key").hidden = false;
}

function fillSelector(bases) {
  const wanted = currentKb();
  $("kb-select").replaceChildren(...bases.map((b) => {
    const option = document.createElement("option");
    option.value = b.id;
    option.textContent = b.id === b.name ? b.id : `${b.id} (${b.name})`;
    return option;
  }));
  $("kb-select").value = bases.some((b) => b.id === wanted) ? wanted : "default";
  $("chat-link").href = chatLink(currentKb());
}

async function loadKnowledgeBases() {
  try {
    const bases = await api("api/knowledge-bases");
    fillSelector(bases);
    $("kbs").replaceChildren(...bases.map((b) => {
      const tr = document.createElement("tr");
      const actions = document.createElement("td");
      actions.className = "actions";
      if (b.private) actions.append(actionButton("New key", `Issue a new chat key for ${b.id}`, "btn--card", (btn) => updateKb(b.id, { newKey: true }, btn)));
      actions.append(actionButton(b.private ? "Make public" : "Make private", `${b.private ? "Make public" : "Make private"}: ${b.id}`, "btn--card",
        (btn) => updateKb(b.id, { private: !b.private }, btn)));
      if (b.id !== "default") actions.append(actionButton("Delete", `Delete knowledge base ${b.id}`, "btn--danger", (btn) => deleteKb(b, btn)));
      tr.append(cell(b.id, "mono"), cell(b.name, "wrap"), cell(b.private ? "Private (key)" : "Public"), cell(String(b.documents), "num"), actions);
      return tr;
    }));
    clearError("kbs-status");
  } catch (err) {
    handle(err, "kbs-status");
  }
}

$("kb-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const button = submitButton(e);
  button.disabled = true;
  try {
    const id = $("kb-id").value.trim();
    const result = await api("api/knowledge-bases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id, name: $("kb-name").value.trim() || null, private: $("kb-private").checked }),
    });
    $("kb-form").reset();
    if (result.chatKey) showKey(id, result.chatKey);
    else $("kb-key").hidden = true;
    say("kbs-status", `Created ${id}.`, "ok");
    await loadKnowledgeBases();
    $("kb-select").value = id;
    changeKb();
  } catch (err) {
    handle(err, "kbs-status");
  } finally {
    button.disabled = false;
  }
});

async function updateKb(id, change, button) {
  if (change.newKey && !window.confirm(`Issue a new key for ${id}? The current key stops working at once.`)) return;
  if (change.private === false && !window.confirm(`Make ${id} public? Anyone who can reach the chat can then ask it.`)) return;
  button.disabled = true;
  try {
    const result = await api(`api/knowledge-bases/${encodeURIComponent(id)}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(change),
    });
    if (result.chatKey) showKey(id, result.chatKey);
    say("kbs-status", `Updated ${id}.`, "ok");
    await loadKnowledgeBases();
  } catch (err) {
    button.disabled = false;
    handle(err, "kbs-status");
  }
}

async function deleteKb(kb, button) {
  if (!window.confirm(`Delete the knowledge base ${kb.id}? It must have no documents.`)) return;
  button.disabled = true;
  try {
    await api(`api/knowledge-bases/${encodeURIComponent(kb.id)}`, { method: "DELETE" });
    say("kbs-status", `Deleted ${kb.id}.`, "ok");
    await loadKnowledgeBases();
    changeKb();
  } catch (err) {
    button.disabled = false;
    handle(err, "kbs-status");
  }
}

/** Everything scoped to the knowledge base reloads when it changes. */
function changeKb() {
  try { sessionStorage.setItem(KB_STORAGE, currentKb()); } catch { /* storage unavailable */ }
  $("chat-link").href = chatLink(currentKb());
  loadDocuments();
  loadSchedules();
  loadUsage();
}

// ---------- usage ----------

const SERIES = [["answered", "Answered"], ["nodata", "Not answered"], ["error", "Error"]];
const dayFormat = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
const dayLabel = (date) => dayFormat.format(new Date(`${date}T00:00:00Z`));
const percent = (part, whole) => (whole ? `${Math.round((part / whole) * 100)}%` : "–");

function kpi(label, value, note) {
  const wrap = document.createElement("div");
  wrap.className = "kpi";
  const dt = document.createElement("dt");
  dt.textContent = label;
  const dd = document.createElement("dd");
  dd.textContent = value;
  if (note) {
    const small = document.createElement("small");
    small.textContent = note;
    dd.append(small);
  }
  wrap.append(dt, dd);
  return wrap;
}

/** A round axis top: 1, 2, 5 × 10^n at or above the busiest day, so the mid line is a whole number too. */
function niceMax(value) {
  if (value <= 2) return 2;
  const step = 10 ** Math.floor(Math.log10(value));
  return [1, 2, 5, 10].map((m) => m * step).find((n) => n >= value && n % 2 === 0) ?? 10 * step;
}

function showTip(bar, day) {
  const tip = $("usage-tip");
  const title = document.createElement("strong");
  title.textContent = `${dayLabel(day.date)}: ${day.questions} question${day.questions === 1 ? "" : "s"}`;
  const rows = [["Answered", day.answered], ["Not answered", day.noData + day.declined], ["Error", day.errors],
    ["Helpful / not", `${day.helpful} / ${day.notHelpful}`]].map(([label, value]) => {
    const row = document.createElement("span");
    const name = document.createElement("span");
    name.textContent = label;
    const num = document.createElement("span");
    num.textContent = String(value);
    row.append(name, num);
    return row;
  });
  tip.replaceChildren(title, ...rows);
  tip.hidden = false;

  // Beside the day (right of it, or left near the end), level with the top of the plot, so it never hides the bar.
  const figure = tip.parentElement.getBoundingClientRect();
  const box = bar.getBoundingClientRect();
  const gap = 8;
  let left = box.right - figure.left + gap;
  if (left + tip.offsetWidth > figure.width) left = Math.max(0, box.left - figure.left - gap - tip.offsetWidth);
  tip.style.left = `${left}px`;
  tip.style.top = `${box.top - figure.top}px`;
}

function hideTip() {
  $("usage-tip").hidden = true;
  document.querySelectorAll(".bar.is-active").forEach((b) => b.classList.remove("is-active"));
}

function drawChart(days) {
  const top = niceMax(Math.max(0, ...days.map((d) => d.questions)));
  $("usage-max").textContent = String(top);
  $("usage-mid").textContent = String(top / 2);
  $("usage-from").textContent = dayLabel(days[0].date);
  $("usage-to").textContent = dayLabel(days[days.length - 1].date);

  const total = days.reduce((n, d) => n + d.questions, 0);
  $("usage-chart").setAttribute("aria-label",
    `Questions per day, ${dayLabel(days[0].date)} to ${dayLabel(days[days.length - 1].date)}: ${total} in total. The daily numbers are in the table below.`);

  $("usage-chart").replaceChildren(...days.map((day) => {
    const bar = document.createElement("div");
    bar.className = "bar";
    const values = { answered: day.answered, nodata: day.noData + day.declined, error: day.errors };
    for (const [key] of SERIES) {
      if (!values[key]) continue;
      const seg = document.createElement("div");
      seg.className = `bar__seg bar__seg--${key}`;
      // Each segment's share of the plot height; the 2px gaps come out of the empty space above.
      seg.style.height = `${(values[key] / top) * 100}%`;
      bar.append(seg);
    }
    bar.addEventListener("pointerenter", () => { hideTip(); bar.classList.add("is-active"); showTip(bar, day); });
    bar.addEventListener("pointerleave", hideTip);
    return bar;
  }));
}

async function loadUsage() {
  try {
    const report = await api(`api/usage?days=${$("usage-days").value}&${kbQuery()}`);
    const notAnswered = report.noData + report.declined;
    $("usage-kpis").replaceChildren(
      kpi("Questions", String(report.questions)),
      kpi("Answered", String(report.answered), percent(report.answered, report.questions)),
      kpi("Not answered", String(notAnswered), percent(notAnswered, report.questions)),
      kpi("Errors", String(report.errors)),
      kpi("Helpful / not", `${report.helpful} / ${report.notHelpful}`),
      kpi("Avg. answer time", report.averageSeconds == null ? "–" : `${report.averageSeconds} s`));

    drawChart(report.days);

    $("usage-days-table").replaceChildren(...[...report.days].reverse().map((d) => {
      const tr = document.createElement("tr");
      tr.append(cell(dayLabel(d.date), "nowrap"), ...[d.questions, d.answered, d.noData + d.declined, d.errors, d.helpful, d.notHelpful]
        .map((n) => cell(String(n), "num")));
      return tr;
    }));

    $("usage-unanswered").replaceChildren(...report.unanswered.map((q) => {
      const tr = document.createElement("tr");
      tr.append(cell(q.question, "wrap"), cell(String(q.count), "num"), cell(when(q.lastAskedAt), "nowrap"));
      return tr;
    }));
    $("usage-unanswered-empty").hidden = report.unanswered.length > 0;
    clearError("usage-status");
  } catch (err) {
    handle(err, "usage-status");
  }
}

// ---------- backup ----------

$("backup-download").addEventListener("click", async (e) => {
  const button = e.currentTarget;
  button.disabled = true;
  say("backup-status", "Preparing the backup…");
  try {
    const headers = new Headers();
    if (apiKey) headers.set("X-Api-Key", apiKey);
    const response = await fetch("api/backup", { headers });
    if (response.status === 401) throw new AuthError("The API key is missing or wrong. Enter it above.");
    if (!response.ok) throw new Error(`The backup failed (${response.status}).`);
    const name = /filename="?([^";]+)"?/.exec(response.headers.get("Content-Disposition") ?? "")?.[1] ?? "chatbot-backup.zip";
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    say("backup-status", `Downloaded ${name}.`, "ok");
    loadAudit();
  } catch (err) {
    handle(err, "backup-status");
  } finally {
    button.disabled = false;
  }
});

$("restore-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const file = $("restore-file").files[0];
  if (!file) return;
  if (!window.confirm(`Replace ALL documents, schedules, ratings and usage with ${file.name}? Anything added since that backup is lost.`)) return;
  const button = submitButton(e);
  button.disabled = true;
  say("backup-status", "Restoring…");
  try {
    const form = new FormData();
    form.append("file", file);
    const result = await api("api/backup/restore", { method: "POST", body: form });
    say("backup-status", `Restored ${result.documents} document(s); ${result.reindexJobs} re-index job(s) queued (see Jobs).`, "ok");
    $("restore-file").value = "";
    loadAll();
  } catch (err) {
    handle(err, "backup-status");
  } finally {
    button.disabled = false;
  }
});

// ---------- activity ----------

async function loadAudit() {
  try {
    const entries = await api("api/audit?limit=100");
    $("audit").replaceChildren(...entries.map((a) => {
      const tr = document.createElement("tr");
      const result = cell(String(a.status), "num");
      if (a.status >= 400) result.classList.add("is-failed");
      tr.append(cell(when(a.at), "nowrap"), cell(a.ip ? `${a.actor} (${a.ip})` : a.actor, "wrap"),
        cell(a.action, "nowrap mono"), cell(a.target, "wrap"), result);
      return tr;
    }));
    $("audit-empty").hidden = entries.length > 0;
    clearError("audit-status");
  } catch (err) {
    handle(err, "audit-status");
  }
}

// ---------- feedback ----------

async function loadFeedback() {
  try {
    const rating = $("feedback-filter").value;
    const list = await api(`api/feedback?limit=100${rating ? `&rating=${rating}` : ""}`);
    $("feedback-totals").textContent = `${list.up} helpful, ${list.down} not helpful in total.`;
    $("feedback").replaceChildren(...list.entries.map((f) => {
      const tr = document.createElement("tr");
      const rated = document.createElement("td");
      rated.append(badge(f.rating === "up" ? "Helpful" : "Not helpful"));
      const answer = f.answer.length > 240 ? `${f.answer.slice(0, 240)}…` : f.answer;
      tr.append(cell(when(f.at), "nowrap"), rated, cell(f.question, "wrap"), cell(answer, "wrap muted"), cell(f.comment, "wrap"));
      return tr;
    }));
    $("feedback-empty").hidden = list.entries.length > 0;
    clearError("key-status");
  } catch (err) {
    handle(err, "key-status");
  }
}

// ---------- key ----------

function loadAll() {
  loadKnowledgeBases();
  ["add-status", "docs-status", "schedules-status", "usage-status", "audit-status", "backup-status", "kbs-status"].forEach(clearError);
  loadStatus();
  loadUsage();
  loadAudit();
  loadSchedules();
  loadJobs();
  loadDocuments();
  loadFeedback();
}

$("key-form").addEventListener("submit", (e) => {
  e.preventDefault();
  apiKey = $("api-key").value.trim();
  try { sessionStorage.setItem(KEY_STORAGE, apiKey); } catch { /* storage unavailable */ }
  $("api-key").value = "";
  say("key-status", apiKey ? "Key in use for this tab." : "No key: works only if the server has none set.", "ok");
  loadAll();
});

$("forget-key").addEventListener("click", () => {
  apiKey = "";
  try { sessionStorage.removeItem(KEY_STORAGE); } catch { /* storage unavailable */ }
  say("key-status", "Key forgotten.");
});

$("refresh-status").addEventListener("click", loadStatus);
$("refresh-jobs").addEventListener("click", loadJobs);
$("refresh-schedules").addEventListener("click", loadSchedules);
$("refresh-docs").addEventListener("click", loadDocuments);
$("refresh-feedback").addEventListener("click", loadFeedback);
$("refresh-usage").addEventListener("click", loadUsage);
$("refresh-kbs").addEventListener("click", loadKnowledgeBases);
$("kb-select").addEventListener("change", changeKb);
try {
  const saved = sessionStorage.getItem(KB_STORAGE);
  if (saved) {
    const option = document.createElement("option");
    option.value = option.textContent = saved;
    $("kb-select").replaceChildren(option);
    $("kb-select").value = saved;
  }
} catch { /* storage unavailable */ }
$("refresh-audit").addEventListener("click", loadAudit);
$("usage-days").addEventListener("change", loadUsage);
$("feedback-filter").addEventListener("change", loadFeedback);

if (apiKey) say("key-status", "Key in use for this tab.", "ok");
loadAll();
