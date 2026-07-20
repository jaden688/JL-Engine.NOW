// web_engine/src/deck.ts
// The JL Engine web deck — the main browser UI, chat-first with a calm telemetry rail.
// Runs the SAME WebEngine core as the browser side panel, so the deck and the panel are
// one product on two surfaces. Backend defaults to Echo (offline); swap to OpenRouter or
// the Python FastAPI backend to generate for real.

import { WebEngine } from "./engine";
import { EchoBackend /*, OpenRouterBackend, ServerFallbackBackend */ } from "./backend";
import type { TurnResult } from "./types";

const engine = new WebEngine({
  backend: new EchoBackend(),
  // Real local generation via the Python engine + Ollama we wired up:
  //   backend: new ServerFallbackBackend({ url: "http://127.0.0.1:8000/quest/chat", operator: "SparkByte" }),
  // Or a hosted model through a key-injecting proxy:
  //   backend: new OpenRouterBackend({ apiBase: "https://your-proxy/api/llm", model: "anthropic/claude-sonnet-5" }),
  safetyOn: true,
});

const $ = <T extends HTMLElement = HTMLElement>(s: string) => document.querySelector(s) as T;
const log = $("#log");
const form = $<HTMLFormElement>("#form");
const input = $<HTMLTextAreaElement>("#msg");
const send = $<HTMLButtonElement>("#send");
const operatorSel = $<HTMLSelectElement>("#operator");
const statusText = $("#statusText");
const statusEl = $("#status");

// ---- chat rendering ----
function bubble(role: "user" | "assistant" | "error", text: string, who?: string) {
  const el = document.createElement("div");
  el.className = `bubble ${role}`;
  if (role === "assistant" && who) {
    const w = document.createElement("div");
    w.className = "who";
    w.textContent = who;
    el.appendChild(w);
  }
  const body = document.createElement("div");
  body.textContent = text;
  el.appendChild(body);
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
  return body;
}

// ---- telemetry rail ----
function setMeter(id: string, value01: number, warnAt = 0.55, hotAt = 0.75) {
  const meter = $(id);
  const bar = meter.firstElementChild as HTMLElement;
  bar.style.width = `${Math.round(Math.max(0, Math.min(1, value01)) * 100)}%`;
  meter.classList.toggle("warn", value01 >= warnAt && value01 < hotAt);
  meter.classList.toggle("hotm", value01 >= hotAt);
}

function renderTelemetry(t: TurnResult["telemetry"]) {
  statusText.textContent = `${t.agent} · ${t.aperture.mode.toLowerCase()}`;
  $("#t-agent").textContent = t.agent;
  $("#t-host").textContent = t.hostedBy;
  $("#t-behavior").textContent = t.behaviorState.name;
  $("#t-gear").textContent = t.cognitiveGear;
  $("#t-rhythm").textContent = `${t.rhythm.mode} (${t.rhythm.index.toFixed(2)})`;

  $("#t-aperture-mode").textContent = t.aperture.mode;
  setMeter("#m-aperture", t.aperture.score, 0.85, 1.1); // aperture "warns" only when flooded
  $("#t-emotion").textContent = t.aperture.emotion ?? "—";
  $("#t-sampling").textContent = `temp ${t.sampling.temp.toFixed(2)} · top_p ${t.sampling.top_p.toFixed(2)}`;

  $("#t-drift-action").textContent = t.drift.action;
  setMeter("#m-drift", t.drift.pressure);

  const s = t.signals;
  $("#s-sentiment").textContent = s.sentiment.toFixed(2);
  $("#s-arousal").textContent = s.arousal.toFixed(2);
  $("#s-confusion").textContent = s.confusion.toFixed(2);
  $("#s-pace").textContent = s.pace.toFixed(2);
  $("#t-mem").textContent = String(t.usedMemoryCount);
}

// ---- interactions ----
async function submitTurn() {
  const text = input.value.trim();
  if (!text) return;
  input.value = "";
  input.style.height = "auto";
  bubble("user", text);
  send.disabled = true;
  try {
    const { reply, telemetry } = await engine.turn(text);
    bubble("assistant", reply, telemetry.agent);
    renderTelemetry(telemetry);
  } catch (err) {
    bubble("error", String(err));
  } finally {
    send.disabled = false;
    input.focus();
  }
}

form.addEventListener("submit", (e) => { e.preventDefault(); submitTurn(); });

// Enter to send, Shift+Enter for newline; auto-grow the textarea.
input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submitTurn(); }
});
input.addEventListener("input", () => {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 160) + "px";
});

operatorSel.addEventListener("change", async () => {
  await engine.setOperator(operatorSel.value);
  statusText.textContent = `${engine.operator} · ready`;
  bubble("assistant", `— switched to ${engine.operator} —`, engine.operator);
});

$("#railToggle").addEventListener("click", () => $("#body").classList.toggle("rail-collapsed"));

// ---- boot ----
engine.whenReady().then(() => {
  operatorSel.innerHTML = "";
  for (const ref of engine.listOperators()) {
    const opt = document.createElement("option");
    opt.value = ref.agentName;
    opt.textContent = ref.agentName;
    if (ref.agentName === engine.operator) opt.selected = true;
    operatorSel.appendChild(opt);
  }
  statusEl.classList.remove("off");
  statusText.textContent = `${engine.operator} · online`;
  bubble("assistant", `Deck online. I'm ${engine.operator}. Switch operators up top, toggle telemetry with ▦. What are we doing?`, engine.operator);
  input.focus();
});
