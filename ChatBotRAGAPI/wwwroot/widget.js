/*
 * Knowledge Chat widget: a chat button on any website, opening the chat in a panel.
 *
 *   <script src="https://bot.example.com/widget.js" defer
 *           data-label="Ask us" data-title="Ask Giriraj Digital" data-position="right"
 *           data-knowledge-base="website"></script>
 *
 * data-knowledge-base picks the knowledge base (default: "default"). For a private one add data-chat-key: it is
 * passed in the frame address's #fragment, which is never sent to any server, but anyone who can view this page can
 * read it, so use private knowledge bases only on pages that are themselves private (an intranet).
 *
 * The chat itself runs in a frame from the bot's own address, so the host page's styles and scripts can't touch it
 * (and it can't touch them). The bot only allows the frame on sites listed in its Widget:AllowedOrigins setting.
 * Everything here is styled through element.style, so it also works on pages whose CSP forbids inline <style>.
 */
(() => {
  "use strict";

  const script = document.currentScript;
  if (!script || window.__knowledgeChatWidget) return;
  window.__knowledgeChatWidget = true;

  const base = new URL(".", script.src);          // the bot's address, including any path base
  const botOrigin = base.origin;
  const label = (script.dataset.label || "Ask us").slice(0, 40);
  const title = (script.dataset.title || "").slice(0, 60);
  const side = script.dataset.position === "left" ? "left" : "right";
  const kb = (script.dataset.knowledgeBase || "").trim();
  const chatKey = (script.dataset.chatKey || "").trim();
  const chatUrl = new URL(`./?embed=1${title ? `&title=${encodeURIComponent(title)}` : ""}${kb ? `&kb=${encodeURIComponent(kb)}` : ""}`
    + (chatKey ? `#key=${encodeURIComponent(chatKey)}` : ""), base).href;

  const ink = "#2b2421", paper = "#fbf8f4", shade = "0 6px 20px -6px rgba(43, 36, 33, 0.35)";
  const phone = window.matchMedia("(max-width: 480px)");

  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  button.setAttribute("aria-expanded", "false");
  button.setAttribute("aria-haspopup", "dialog");
  Object.assign(button.style, {
    position: "fixed", bottom: "20px", [side]: "20px", zIndex: "2147483000",
    minHeight: "48px", padding: "0 20px", border: "0", borderRadius: "8px",
    background: ink, color: paper, boxShadow: shade, cursor: "pointer",
    font: "500 15px/1 system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
  });

  const panel = document.createElement("div");
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-label", title || "Chat");
  Object.assign(panel.style, {
    position: "fixed", zIndex: "2147483001", display: "none", overflow: "hidden",
    background: paper, boxShadow: shade, border: "1px solid rgba(43, 36, 33, 0.12)",
  });

  function place() {
    const full = phone.matches;
    Object.assign(panel.style, full
      ? { top: "0", bottom: "0", left: "0", right: "0", width: "auto", height: "auto", borderRadius: "0" }
      : { top: "auto", bottom: "84px", left: side === "left" ? "20px" : "auto", right: side === "right" ? "20px" : "auto",
          width: "380px", height: "min(640px, calc(100vh - 104px))", borderRadius: "10px" });
  }

  let frame = null;
  let open = false;

  function show() {
    if (!frame) {
      // Created on first use: a page with the widget loads nothing from the bot until someone opens it.
      frame = document.createElement("iframe");
      frame.src = chatUrl;
      frame.title = title || "Chat";
      frame.setAttribute("referrerpolicy", "no-referrer");
      Object.assign(frame.style, { width: "100%", height: "100%", border: "0", display: "block" });
      panel.append(frame);
    }
    place();
    panel.style.display = "block";
    button.setAttribute("aria-expanded", "true");
    open = true;
    frame.focus();
  }

  function hide() {
    if (!open) return;
    panel.style.display = "none";
    button.setAttribute("aria-expanded", "false");
    open = false;
    button.focus();
  }

  button.addEventListener("click", () => (open ? hide() : show()));
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") hide(); });
  // The chat's close button and Escape inside the frame post this; anything from another origin is ignored.
  window.addEventListener("message", (e) => {
    if (e.origin === botOrigin && e.data && e.data.type === "knowledge-chat:close") hide();
  });
  phone.addEventListener("change", () => { if (open) place(); });

  const mount = () => document.body.append(panel, button);
  if (document.body) mount();
  else document.addEventListener("DOMContentLoaded", mount);
})();
