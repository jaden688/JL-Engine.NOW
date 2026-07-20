// web_engine/src/engine.ts
// The browser-native JL Engine core — a faithful port of engine_core.process_turn:
//
//   signals → behavior grid → emotional aperture → cognitive gear → drift → rhythm
//           → sampling bias → backend.generate → memory store
//
// Runs entirely in the tab. Loads the same MPF operator payloads as the Julia and
// Python engines. Falls back to a server backend only when explicitly configured.

import { scoreSignals, deriveTrigger } from "./signals";
import { BehaviorGrid } from "./behavior";
import { DEFAULT_BEHAVIOR_CONFIG } from "./default_behavior";
import { EmotionalAperture } from "./aperture";
import { DriftRegulator } from "./drift";
import { RhythmEngine } from "./rhythm";
import { WebMemory } from "./memory";
import { OperatorRegistry, bootPrompt, type OperatorSource } from "./operators";
import type { Backend } from "./backend";
import { EchoBackend } from "./backend";
import type { ChatMessage, OperatorPayload, TurnResult } from "./types";

export interface EngineOptions {
  backend?: Backend;
  operatorSource?: OperatorSource; // omit → single bundled operator
  behaviorConfigUrl?: string;      // omit → default centered grid
  safetyOn?: boolean;
}

function cognitiveGear(focus: number, overload: number): string {
  if (overload > 0.5) return "GUARD";      // pull back, stabilize
  if (focus > 0.6) return "SPUR";          // precise, task-locked
  if (focus < 0.3) return "DRIFT";         // loose, associative
  return "TASK_FLOW";
}

export class WebEngine {
  private backend: Backend;
  private safetyOn: boolean;
  private grid!: BehaviorGrid;
  private aperture = new EmotionalAperture();
  private drift = new DriftRegulator();
  private rhythm = new RhythmEngine();
  private memory = new WebMemory();
  private registry: OperatorRegistry;
  private behaviorConfigUrl?: string;

  private currentName = "SparkByte";
  private currentPayload: OperatorPayload | null = null;
  private lastRhythmMode = "flop";
  private gait = "walk";
  private ready: Promise<void>;

  constructor(opts: EngineOptions = {}) {
    this.backend = opts.backend ?? new EchoBackend();
    this.safetyOn = opts.safetyOn ?? true;
    this.registry = new OperatorRegistry(opts.operatorSource);
    this.behaviorConfigUrl = opts.behaviorConfigUrl;
    this.ready = this.init();
  }

  private async init(): Promise<void> {
    const names = await this.registry.load();
    let cfg: object = DEFAULT_BEHAVIOR_CONFIG;
    if (this.behaviorConfigUrl) {
      try { cfg = await (await fetch(this.behaviorConfigUrl)).json(); } catch { /* keep default grid */ }
    }
    this.grid = new BehaviorGrid(cfg);
    if (!names.includes(this.currentName)) this.currentName = names[0];
    await this.setOperator(this.currentName);
  }

  async whenReady(): Promise<void> { return this.ready; }

  listOperators() { return this.registry.list(); }
  get operator(): string { return this.currentName; }

  async setOperator(name: string): Promise<void> {
    this.currentName = name;
    this.currentPayload = await this.registry.resolve(name);
    this.aperture.setPalette(this.currentPayload.emotion_palette);
    this.aperture.reset();
    this.lastRhythmMode = "flop";
    this.gait = "walk";
  }

  /** Process one conversational turn through the full engine loop. */
  async turn(userText: string): Promise<TurnResult> {
    await this.ready;

    // 1) Signals
    const signals = scoreSignals(userText);
    const trigger = deriveTrigger(signals);

    // 2) Behavior grid transition
    this.grid.transitionByTrigger(trigger);
    const behaviorState = this.grid.getCurrentState();
    const blend = this.grid.getCurrentBlend();

    // 3) Emotional aperture
    this.aperture.updateFromSignals({ behaviorState, signals });
    let aperture = this.aperture.getState();

    // 4) Cognitive gear
    const gear = cognitiveGear(this.aperture.focusLevel(), this.aperture.overloadLevel());

    // 5) Drift pressure + corrective action
    const pressure = this.drift.calculate({
      signals,
      aperture,
      expressiveness: blend.expressiveness,
      memoryDensity: signals.memoryDensity,
    });
    const driftResponse = this.drift.getResponse(pressure);
    if (driftResponse.forceGait) this.gait = driftResponse.forceGait;

    // Safety gating drags the grid down when drift is high.
    if (this.safetyOn && driftResponse.action === "recenter") {
      this.grid.transitionByTrigger(null, { level: "safety_block", weight: 1.0 });
      this.aperture.injectDriftBias(-0.2);
      aperture = this.aperture.getState();
    }

    // 6) Rhythm
    const rhythm = this.rhythm.compute({
      lastMode: this.lastRhythmMode,
      trigger,
      gait: this.gait,
      behaviorState,
      driftPressure: pressure,
      safetyOn: this.safetyOn,
    });
    this.lastRhythmMode = rhythm.mode;
    if (driftResponse.forceRhythm) this.lastRhythmMode = driftResponse.forceRhythm;

    // 7) Sampling bias from the aperture
    const sampling = aperture.samplingBias;

    // 8) Build messages: boot prompt + memory + this turn
    const history = await this.memory.asMessages(this.currentName);
    const messages: ChatMessage[] = [
      { role: "system", content: bootPrompt(this.currentPayload!) },
      { role: "system", content:
        `[engine state] behavior=${behaviorState.name} aperture=${aperture.mode} ` +
        `gear=${gear} rhythm=${rhythm.mode} drift=${pressure.toFixed(2)}` },
      ...history,
      { role: "user", content: userText },
    ];

    // 9) Generate
    const reply = await this.backend.generate(messages, { temperature: sampling.temp, top_p: sampling.top_p });

    // 10) Store memory
    await this.memory.store(this.currentName, userText, reply);

    return {
      reply,
      telemetry: {
        agent: this.currentName,
        signals,
        behaviorState,
        aperture,
        cognitiveGear: gear,
        drift: driftResponse,
        rhythm,
        sampling,
        usedMemoryCount: history.length / 2,
        hostedBy: this.backend.name === "server-fallback" ? "server-fallback" : "browser",
      },
    };
  }
}
