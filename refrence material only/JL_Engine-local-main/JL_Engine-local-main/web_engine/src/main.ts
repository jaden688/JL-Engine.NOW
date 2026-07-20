// web_engine/src/main.ts
// Minimal demo harness: wires WebEngine to a chat box + a live telemetry HUD.
// Runs offline out of the box (EchoBackend + bundled operator). Swap the backend
// and operatorSource below to go live.

import { WebEngine } from "./engine";
import { EchoBackend /*, OpenRouterBackend, ServerFallbackBackend */ } from "./backend";
import type { TurnResult } from "./types";

const engine = new WebEngine({
  backend: new EchoBackend(),
  // To load the real operators, serve the Python engine's data dir and point here:
  //   operatorSource: { baseUrl: "/operators/", registryFile: "JL_Agents.mpf.json" },
  //   behaviorConfigUrl: "/operators/behavior_states.json",
  // To go live:
  //   backend: new OpenRouterBackend({ apiBase: "/api/llm", model: "anthropic/claude-sonnet-5" }),
  // Hybrid heavy work → hand up to the Julia/Python server:
  //   backend: new ServerFallbackBackend({ url: "http://127.0.0.1:8081/turn", operator: "SparkByte" }),
  safetyOn: true,
});

const $ = (sel: string) => document.querySelector(sel)!;
const log = $("#log") as HTMLDivElement;
const hud = $("#hud") as HTMLPreElement;
const input = $("#msg") as HTMLInputElement;
const form = $("#form") as HTMLFormElement;
const operatorSel = $("#operator") as HTMLSelectElement;

// Expose for debugging / verification in the browser console.
(window as unknown as { engine: WebEngine }).engine = engine;

operatorSel.addEventListener("change", async () => {
  await engine.setOperator(operatorSel.value);
  bubble("assistant", `— switched to ${engine.operator} —`);
});

function bubble(role: string, text: string) {
  const el = document.createElement("div");
  el.className = `bubble ${role}`;
  el.textContent = text;
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
}

function renderHud(t: TurnResult["telemetry"]) {
  hud.textContent = JSON.stringify(
    {
      operator: t.agent,
      hostedBy: t.hostedBy,
      behavior: t.behaviorState.name,
      aperture: `${t.aperture.mode} (${t.aperture.score.toFixed(2)})`,
      emotion: t.aperture.emotion,
      gear: t.cognitiveGear,
      rhythm: `${t.rhythm.mode} idx=${t.rhythm.index.toFixed(2)}`,
      drift: `${t.drift.action} ${t.drift.pressure.toFixed(2)}`,
      sampling: `temp=${t.sampling.temp.toFixed(2)} top_p=${t.sampling.top_p.toFixed(2)}`,
      signals: {
        sentiment: t.signals.sentiment.toFixed(2),
        arousal: t.signals.arousal.toFixed(2),
        confusion: t.signals.confusion.toFixed(2),
        pace: t.signals.pace.toFixed(2),
      },
      memoryTurns: t.usedMemoryCount,
    },
    null,
    2,
  );
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = input.value.trim();
  if (!text) return;
  input.value = "";
  bubble("user", text);
  try {
    const { reply, telemetry } = await engine.turn(text);
    bubble("assistant", reply);
    renderHud(telemetry);
  } catch (err) {
    bubble("error", String(err));
  }
});

engine.whenReady().then(() => {
  operatorSel.innerHTML = "";
  for (const ref of engine.listOperators()) {
    const opt = document.createElement("option");
    opt.value = ref.agentName;
    opt.textContent = ref.agentName;
    if (ref.agentName === engine.operator) opt.selected = true;
    operatorSel.appendChild(opt);
  }
  bubble("assistant", `Browser engine online. Operator: ${engine.operator}. Type to drive the loop.`);
});
