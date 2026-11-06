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
    else if (jobsTimer) { jobsTimer = null; loadDocuments(); }
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
  ["add-status", "docs-status"].forEach(clearError);
  loadStatus();
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
$("refresh-docs").addEventListener("click", loadDocuments);
$("refresh-feedback").addEventListener("click", loadFeedback);
$("feedback-filter").addEventListener("change", loadFeedback);

if (apiKey) say("key-status", "Key in use for this tab.", "ok");
loadAll();
