// Admin page: documents, ingestion jobs, readiness and feedback, over the same API as Swagger.
// Plain JS, no dependencies. Server text is only ever inserted with textContent / text nodes, never parsed as HTML.
"use strict";

const KEY_STORAGE = "knowledge-chat-admin-key";
const $ = (id) => document.getElementById(id);

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
    const docs = await api("api/documents");
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
    await api(`api/documents?source=${encodeURIComponent(source)}`, { method: "DELETE" });
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
      body: JSON.stringify({ url: $("crawl-url").value.trim(), maxPages, removeMissing: maxPages > 1 && $("crawl-remove").checked }),
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
    const schedules = await api("api/schedules");
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
    const report = await api(`api/usage?days=${$("usage-days").value}`);
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
  ["add-status", "docs-status", "schedules-status", "usage-status"].forEach(clearError);
  loadStatus();
  loadUsage();
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
$("usage-days").addEventListener("change", loadUsage);
$("feedback-filter").addEventListener("change", loadFeedback);

if (apiKey) say("key-status", "Key in use for this tab.", "ok");
loadAll();
