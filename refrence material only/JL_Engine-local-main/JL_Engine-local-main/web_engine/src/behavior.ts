// web_engine/src/behavior.ts
// Port of behavior_engine.py — the 5x4 behavior grid + trigger-driven transitions.
// Loads the SAME behavior_states.json the Python engine uses.

import type { BehaviorState } from "./types";
import { DEFAULT_BEHAVIOR_CONFIG } from "./default_behavior";

interface RawState {
  id: string; name: string; expressiveness: number;
  pacing: string; tone_bias: string; memory_strictness: string;
}
interface GridConfig {
  grid_dimensions?: { rows: number; columns: number };
  trigger_mappings?: Record<string, [number, number]>;
  states?: RawState[][];
}

function toState(r: Partial<RawState>): BehaviorState {
  return {
    id: r.id ?? "0,0",
    name: r.name ?? "Unknown",
    expressiveness: typeof r.expressiveness === "number" ? r.expressiveness : 0.5,
    pacing: r.pacing ?? "normal",
    toneBias: r.tone_bias ?? "neutral",
    memoryStrictness: r.memory_strictness ?? "medium",
  };
}

export class BehaviorGrid {
  private states: BehaviorState[][];
  private triggers: Record<string, [number, number]>;
  private rows: number;
  private cols: number;
  private row = 2;   // start centered (Engaged)
  private col = 1;

  constructor(config: GridConfig) {
    const rawRows = config.states ?? [];
    this.states = rawRows.map((r) => r.map(toState));
    if (this.states.length === 0) {
      this.states = Array.from({ length: 5 }, () =>
        Array.from({ length: 4 }, () => toState({})),
      );
    }
    this.rows = this.states.length;
    this.cols = this.states[0]?.length ?? 4;
    this.triggers = config.trigger_mappings ?? {};
  }

  static fromDefaults(): BehaviorGrid {
    return new BehaviorGrid(DEFAULT_BEHAVIOR_CONFIG);
  }

  transitionByTrigger(trigger: string | null, gatingAdvice?: { level?: string; weight?: number } | null): void {
    // Safety gating drags the grid toward disciplined/low rows.
    if (gatingAdvice?.level === "safety_block") {
      this.setCoords(Math.max(0, this.row - 1), 0);
      return;
    }
    if (trigger && this.triggers[trigger]) {
      const [r, c] = this.triggers[trigger];
      this.setCoords(r, c);
    }
  }

  setCoords(r: number, c: number): void {
    this.row = Math.max(0, Math.min(this.rows - 1, r));
    this.col = Math.max(0, Math.min(this.cols - 1, c));
  }

  get currentRow(): number { return this.row; }
  get currentCol(): number { return this.col; }

  getCurrentState(): BehaviorState {
    return this.states[this.row][this.col];
  }

  /** Blend of the current cell with its neighbors — soft expressiveness. */
  getCurrentBlend(): { expressiveness: number; neighbors: string[] } {
    const cur = this.getCurrentState();
    const neigh: BehaviorState[] = [];
    for (const [dr, dc] of [[-1, 0], [1, 0], [0, -1], [0, 1]]) {
      const nr = this.row + dr, nc = this.col + dc;
      if (nr >= 0 && nr < this.rows && nc >= 0 && nc < this.cols) neigh.push(this.states[nr][nc]);
    }
    const avg = neigh.length
      ? neigh.reduce((a, s) => a + s.expressiveness, 0) / neigh.length
      : cur.expressiveness;
    return {
      expressiveness: cur.expressiveness * 0.7 + avg * 0.3,
      neighbors: neigh.map((s) => s.id),
    };
  }
}
