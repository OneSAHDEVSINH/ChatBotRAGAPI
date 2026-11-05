// Chat UI for /api/chat/ask/stream. Plain JS, no dependencies.
// All server text is inserted with textContent / text nodes, never parsed as HTML.
"use strict";

const STORAGE_KEY = "knowledge-chat";
const MAX_HISTORY = 20; // messages sent back for follow-ups (the server uses only its last Rag:MaxHistoryMessages)
const NO_DATA = "I don't have enough data.";

const els = {
  conversation: document.getElementById("conversation"),
  empty: document.getElementById("empty"),
  form: document.getElementById("composer"),
  input: document.getElementById("question"),
  send: document.getElementById("send"),
  stop: document.getElementById("stop"),
  newChat: document.getElementById("new-chat"),
  tplUser: document.getElementById("tpl-user"),
  tplAssistant: document.getElementById("tpl-assistant"),
};

/** Completed turns: { question, answer, sources, context } — what is rendered and what history is built from. */
let turns = load();
let controller = null; // AbortController of the request in flight

// ---------- persistence (per tab; the page works without it) ----------

function load() {
  try {
    const saved = JSON.parse(sessionStorage.getItem(STORAGE_KEY) || "[]");
    return Array.isArray(saved) ? saved : [];
  } catch {
    return [];
  }
}

function save() {
  try { sessionStorage.setItem(STORAGE_KEY, JSON.stringify(turns)); } catch { /* storage unavailable */ }
}

// ---------- rendering ----------

function scrollToEnd() {
  els.conversation.scrollTop = els.conversation.scrollHeight;
}

function addUserMessage(text) {
  const node = els.tplUser.content.firstElementChild.cloneNode(true);
  node.querySelector(".bubble").textContent = text;
  els.conversation.appendChild(node);
  return node;
}

function addAssistantMessage() {
  const node = els.tplAssistant.content.firstElementChild.cloneNode(true);
  els.conversation.appendChild(node);
  return node;
}

function setStatus(msg, text) {
  const status = msg.querySelector(".status");
  status.hidden = text === null;
  if (text !== null) status.querySelector(".status-text").textContent = text;
}

/** Renders answer text, turning [n] into buttons that open source n (only when source n exists). */
function renderAnswer(msg, text, sourceCount) {
  const answer = msg.querySelector(".answer");
  answer.replaceChildren();
  const pattern = /\[(\d+)\]/g;
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    const n = Number(match[1]);
    if (n < 1 || n > sourceCount) continue; // not a citation we can resolve: leave as text
    answer.append(document.createTextNode(text.slice(last, match.index)));
    const cite = document.createElement("button");
    cite.type = "button";
    cite.className = "cite";
    cite.textContent = String(n);
    cite.title = `Show source ${n}`;
    cite.dataset.source = String(n);
    answer.append(cite);
    last = match.index + match[0].length;
  }
  answer.append(document.createTextNode(text.slice(last)));
  msg.classList.toggle("nodata", text.trim() === NO_DATA);
}

/** "https://site.com/about/team/" -> "site.com/about/team"; file names unchanged. */
function sourceLabel(source) {
  try {
    const url = new URL(source);
    if (url.protocol === "http:" || url.protocol === "https:") {
      const path = decodeURIComponent(url.pathname).replace(/\/$/, "");
      return url.host.replace(/^www\./, "") + path + url.search;
    }
  } catch { /* not a URL */ }
  return source;
}

function renderSources(msg, sources, context) {
  const list = msg.querySelector(".sources");
  list.replaceChildren();
  list.hidden = sources.length === 0;

  sources.forEach((s, i) => {
    const item = document.createElement("li");
    item.className = "source";
    item.id = `${msg.dataset.id}-src-${s.id}`;

    const details = document.createElement("details");
    const summary = document.createElement("summary");

    const num = document.createElement("span");
    num.className = "source-num";
    num.textContent = String(s.id);

    const name = document.createElement("span");
    name.className = "source-name";
    name.textContent = sourceLabel(s.source);
    name.title = s.source;

    const match = document.createElement("span");
    match.className = "source-match";
    match.textContent = s.match === "both" ? "meaning + keywords" : s.match === "keyword" ? "keywords" : "meaning";
    match.title = s.score == null ? "Found by keyword search" : `Similarity ${s.score}`;

    summary.append(num, name, match);

    const passage = document.createElement("p");
    passage.className = "passage";
    passage.textContent = context[i] ?? "";
    if (/^https?:\/\//i.test(s.source)) {
      const link = document.createElement("a");
      link.href = s.source;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      link.textContent = "Open page ↗";
      passage.append(document.createElement("br"), document.createElement("br"), link);
    }

    details.append(summary, passage);
    item.append(details);
    list.append(item);
  });
}

function renderTurn(turn) {
  addUserMessage(turn.question);
  const msg = addAssistantMessage();
  msg.dataset.id = `t${els.conversation.children.length}`;
  setStatus(msg, null);
  renderSources(msg, turn.sources, turn.context);
  renderAnswer(msg, turn.answer, turn.sources.length);
}

function renderAll() {
  els.conversation.replaceChildren(els.empty);
  els.empty.hidden = turns.length > 0;
  turns.forEach(renderTurn);
  scrollToEnd();
}

// Citation click: open that source's passage and bring it into view.
els.conversation.addEventListener("click", (e) => {
  const cite = e.target.closest(".cite");
  if (!cite) return;
  const msg = cite.closest(".msg");
  const item = msg.querySelector(`#${CSS.escape(msg.dataset.id)}-src-${cite.dataset.source}`);
  if (!item) return;
  item.querySelector("details").open = true;
  item.classList.remove("flash");
  void item.offsetWidth; // restart the animation
  item.classList.add("flash");
  item.scrollIntoView({ block: "nearest", behavior: "smooth" });
});

// ---------- talking to the API ----------

function history() {
  return turns
    .flatMap((t) => [{ role: "user", content: t.question }, { role: "assistant", content: t.answer }])
    .filter((m) => m.content.trim().length > 0)
    .slice(-MAX_HISTORY);
}

/** Error text from a non-2xx response: problem+json detail/title, a JSON string, or the status. */
async function errorMessage(response) {
  if (response.status === 429) return "Too many requests. Wait a moment, then ask again.";
  // A server failure is temporary from the reader's side: say what broke, then what to do.
  const retry = response.status >= 500 ? " Try again in a moment." : "";
  try {
    const body = await response.json();
    if (typeof body === "string") return body;
    if (body?.detail || body?.title) return (body.detail || body.title) + retry;
  } catch { /* not JSON */ }
  if (response.status === 503) return "The answering service is unavailable right now. Try again in a moment.";
  return `The request failed (${response.status}).${retry}`;
}

/** Parses a text/event-stream body, calling onEvent(type, data) for each event. */
async function readEvents(response, onEvent) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true }).replace(/\r\n?/g, "\n");
    let end;
    while ((end = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      let type = "message";
      const data = [];
      for (const line of block.split("\n")) {
        if (line.startsWith("event:")) type = line.slice(6).trim();
        else if (line.startsWith("data:")) data.push(line.slice(5).replace(/^ /, ""));
      }
      if (data.length) onEvent(type, JSON.parse(data.join("\n")));
    }
  }
}

async function ask(question) {
  els.empty.hidden = true;
  addUserMessage(question);
  const msg = addAssistantMessage();
  msg.dataset.id = `t${els.conversation.children.length}`;
  scrollToEnd();

  controller = new AbortController();
  setBusy(true);

  let sources = [];
  let context = [];
  let text = "";
  let finished = false;

  try {
    const response = await fetch("api/chat/ask/stream", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
      body: JSON.stringify({ question, history: history() }),
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(await errorMessage(response));

    await readEvents(response, (type, data) => {
      switch (type) {
        case "sources":
          sources = data;
          setStatus(msg, "Writing answer…");
          break;
        case "context":
          context = data;
          renderSources(msg, sources, context);
          break;
        case "delta":
          text += data.text;
          setStatus(msg, null);
          renderAnswer(msg, text, sources.length);
          break;
        case "done":
          text = data.answer;
          finished = true;
          renderAnswer(msg, text, sources.length);
          break;
        case "error":
          throw new Error(data.message || "The answer could not be generated.");
      }
      scrollToEnd();
    });

    if (!finished) throw new Error("The connection closed before the answer was complete.");

    turns.push({ question, answer: text, sources, context });
    save();
  } catch (err) {
    setStatus(msg, null);
    if (err.name === "AbortError") {
      // Keep what was written, but leave the turn out of the history the server sees next time.
      const note = document.createElement("div");
      note.className = "stopped-note";
      note.textContent = text ? "Stopped." : "Stopped before an answer was written.";
      msg.querySelector(".bubble").append(note);
    } else {
      msg.classList.add("error");
      msg.querySelector(".sources").hidden = true;
      renderAnswer(msg, err.message || "Something went wrong.", 0);
    }
  } finally {
    controller = null;
    setBusy(false);
    scrollToEnd();
  }
}

// ---------- composer ----------

function setBusy(busy) {
  els.send.hidden = busy;
  els.stop.hidden = !busy;
  els.input.disabled = false; // keep typing the next question while one is answered
  updateSend();
  if (!busy) els.input.focus();
}

function updateSend() {
  els.send.disabled = controller !== null || els.input.value.trim() === "";
}

function autoGrow() {
  els.input.style.height = "auto";
  els.input.style.height = `${Math.min(els.input.scrollHeight, 200)}px`;
}

els.form.addEventListener("submit", (e) => {
  e.preventDefault();
  const question = els.input.value.trim();
  if (!question || controller) return;
  els.input.value = "";
  autoGrow();
  ask(question);
});

els.input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    els.form.requestSubmit();
  }
});

els.input.addEventListener("input", () => { autoGrow(); updateSend(); });
els.stop.addEventListener("click", () => controller?.abort());

els.newChat.addEventListener("click", () => {
  controller?.abort();
  turns = [];
  save();
  renderAll();
  els.input.focus();
});

renderAll();
updateSend();
els.input.focus();
