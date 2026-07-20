// extension/sidepanel.ts
// The operator panel. WebEngine (the ported behavioral loop) runs right here;
// page_hands gives it a body in the real browser; the activity timeline makes
// every browser action observable — you see what it touches, live.
//
// Two ways to act:
//  1. Slash commands — deterministic manual control (/go /read /click /fill /tabs).
//  2. Chat — the engine loop runs; when a real LLM backend is wired, replies
//     containing a ```tool {…}``` block get executed against the page and the
//     result is fed back for one follow-up turn.

import { WebEngine } from "../src/engine";
import { EchoBackend /*, OpenRouterBackend */ } from "../src/backend";
import type { TurnResult } from "../src/types";
import { readPage, clickOn, fillField, navigate, listTabs, type PageInfo, type ActionResult } from "./page_hands";

const engine = new WebEngine({
  backend: new EchoBackend(),
  // Go live: backend: new OpenRouterBackend({ apiBase: "https://your-proxy/api/llm", model: "anthropic/claude-sonnet-5" }),
  safetyOn: true,
});

const log = document.querySelector("#log") as HTMLDivElement;
const acts = document.querySelector("#acts") as HTMLDivElement;
const hud = document.querySelector("#hud") as HTMLPreElement;
const stateEl = document.querySelector("#state") as HTMLSpanElement;
const operatorSel = document.querySelector("#operator") as HTMLSelectElement;
const input = document.querySelector("#msg") as HTMLInputElement;
const form = document.querySelector("#form") as HTMLFormElement;

operatorSel.addEventListener("change", async () => {
  await engine.setOperator(operatorSel.value);
  stateEl.textContent = `${engine.operator} · online`;
  bubble("tool", `operator → ${engine.operator}`);
});

let lastPage: PageInfo | null = null;

function bubble(role: "user" | "assistant" | "tool" | "error", text: string) {
  const el = document.createElement("div");
  el.className = `bubble ${role}`;
  el.textContent = text;
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
}

/** Push one browser action onto the live observation timeline. */
function observe(r: ActionResult) {
  acts.querySelector(".empty")?.remove();
  const row = document.createElement("div");
  row.className = `act ${r.ok ? "ok" : "miss"}`;
  const time = new Date().toLocaleTimeString([], { hour12: false });
  row.innerHTML = `<span class="dot"></span><span class="verb">${r.action}</span><span class="txt"></span>`;
  (row.querySelector(".txt") as HTMLElement).textContent = `${r.detail}  ·  ${time}`;
  acts.appendChild(row);
  acts.scrollTop = acts.scrollHeight;
}

function renderHud(t: TurnResult["telemetry"]) {
  stateEl.textContent = `${t.agent} · ${t.aperture.mode} · ${t.rhythm.mode}`;
  hud.textContent =
    `behavior=${t.behaviorState.name}  gear=${t.cognitiveGear}  drift=${t.drift.action} ${t.drift.pressure.toFixed(2)}\n` +
    `aperture=${t.aperture.score.toFixed(2)} (${t.aperture.emotion ?? "—"})  ` +
    `sampling temp=${t.sampling.temp.toFixed(2)} top_p=${t.sampling.top_p.toFixed(2)}\n` +
    `signals: sent=${t.signals.sentiment.toFixed(2)} arousal=${t.signals.arousal.toFixed(2)} ` +
    `confusion=${t.signals.confusion.toFixed(2)} pace=${t.signals.pace.toFixed(2)}  mem=${t.usedMemoryCount}`;
}

const HELP = [
  "/go <url>           navigate the active tab",
  "/read               read the current page into context",
  "/click <sel|text>   click a CSS selector or exact link/button text",
  "/fill <sel> :: <v>  fill a field",
  "/tabs               list open tabs",
  "anything else       talk to the operator (page context attached after /read)",
].join("\n");

async function runCommand(cmd: string, rest: string): Promise<void> {
  switch (cmd) {
    case "/help":
      bubble("tool", HELP);
      return;
    case "/go": {
      const r = await navigate(rest);
      observe(r);
      bubble("tool", r.detail);
      return;
    }
    case "/read": {
      const r = await readPage();
      lastPage = r;
      observe(r);
      bubble("tool", `read: ${r.title}\n${r.url}\n— ${r.text.length} chars, ${r.links.length} links. Page context attached to chat.`);
      return;
    }
    case "/click": {
      const r = await clickOn(rest);
      observe(r);
      bubble(r.ok ? "tool" : "error", r.detail);
      return;
    }
    case "/fill": {
      const [sel, ...valParts] = rest.split("::");
      if (!sel || valParts.length === 0) { bubble("error", "Usage: /fill <selector> :: <value>"); return; }
      const r = await fillField(sel.trim(), valParts.join("::").trim());
      observe(r);
      bubble(r.ok ? "tool" : "error", r.detail);
      return;
    }
    case "/tabs": {
      const r = await listTabs();
      observe(r);
      bubble("tool", r.tabs.join("\n"));
      return;
    }
    default:
      bubble("error", `Unknown command ${cmd}.\n${HELP}`);
  }
}

/** If the model asked for a tool via ```tool {"name":..., ...}```, run it — observed. */
async function maybeRunToolCall(reply: string): Promise<string | null> {
  const m = reply.match(/```tool\s*({[\s\S]*?})\s*```/);
  if (!m) return null;
  try {
    const call = JSON.parse(m[1]) as { name: string; selector?: string; value?: string; url?: string; text?: string };
    let r: ActionResult;
    switch (call.name) {
      case "read_page": { const p = await readPage(); lastPage = p; r = p; break; }
      case "click": r = await clickOn(call.selector ?? call.text ?? ""); break;
      case "fill": r = await fillField(call.selector ?? "", call.value ?? ""); break;
      case "navigate": r = await navigate(call.url ?? ""); break;
      case "list_tabs": r = await listTabs(); break;
      default: return `Unknown tool "${call.name}".`;
    }
    observe(r);
    if (call.name === "read_page" && lastPage) return `page: ${lastPage.title}\n${lastPage.text.slice(0, 4000)}`;
    return r.detail;
  } catch (e) {
    return `Tool call failed: ${e}`;
  }
}

async function chatTurn(text: string): Promise<void> {
  const withContext = lastPage
    ? `${text}\n\n[active page: ${lastPage.title} — ${lastPage.url}]\n${lastPage.text.slice(0, 3000)}`
    : text;
  const { reply, telemetry } = await engine.turn(withContext);
  bubble("assistant", reply);
  renderHud(telemetry);

  const toolResult = await maybeRunToolCall(reply);
  if (toolResult) {
    bubble("tool", toolResult);
    const followup = await engine.turn(`[tool result]\n${toolResult.slice(0, 4000)}`);
    bubble("assistant", followup.reply);
    renderHud(followup.telemetry);
  }
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = input.value.trim();
  if (!text) return;
  input.value = "";
  bubble("user", text);
  try {
    if (text.startsWith("/")) {
      const [cmd, ...rest] = text.split(" ");
      await runCommand(cmd, rest.join(" ").trim());
    } else {
      await chatTurn(text);
    }
  } catch (err) {
    bubble("error", String(err));
  }
});

document.querySelectorAll<HTMLButtonElement>("#toolbar button").forEach((b) =>
  b.addEventListener("click", () => {
    input.value = b.dataset.cmd ?? "";
    form.requestSubmit();
  }),
);

engine.whenReady().then(() => {
  // Populate the operator picker from the loaded roster.
  operatorSel.innerHTML = "";
  for (const ref of engine.listOperators()) {
    const opt = document.createElement("option");
    opt.value = ref.agentName;
    opt.textContent = ref.agentName;
    if (ref.agentName === engine.operator) opt.selected = true;
    operatorSel.appendChild(opt);
  }
  stateEl.textContent = `${engine.operator} · online`;
  bubble("assistant", `${engine.operator} in the browser. /read to give me the page, /help for hands. Switch operators up top.`);
});
