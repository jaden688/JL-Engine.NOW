// web_engine/src/types.ts
// Shared types for the browser-native JL Engine loop.
// The operator payload format is the SAME MPF JSON the Julia/Python engines load —
// that shared contract is the convergence point across all three hosts.

export interface TurnSignals {
  sentiment: number;       // -1..1
  arousal: number;         // 0..1
  directive: boolean;      // user wants brevity/precision
  confusion: number;       // 0..1
  pace: number;            // 0..1 (0=slow, 1=fast)
  memoryDensity: number;   // 0..1 suggested memory pressure
}

export interface BehaviorState {
  id: string;
  name: string;
  expressiveness: number;
  pacing: string;
  toneBias: string;
  memoryStrictness: string;
}

export interface ApertureState {
  score: number;           // 0..1
  mode: "SEALED" | "LIMITED" | "OPEN" | "FLOODED";
  emotion: string | null;
  samplingBias: { temp: number; top_p: number };
}

export interface RhythmInfo {
  mode: string;
  index: number;           // 0..1 momentum
  gait: string;
}

export interface DriftResponse {
  pressure: number;        // 0..1
  action: "hold" | "settle" | "brake" | "recenter";
  forceGait: string | null;
  forceRhythm: string | null;
}

/** One entry in the MPF registry (JL_Agents.mpf.json). */
export interface OperatorRef {
  agentName: string;
  classification: string;
  defaultMemoryMode?: string;
  defaultBackendId?: string;
  driveType?: string | null;
  tags?: string[];
  operatorFile?: string;   // was jl_agent_file — pointer to the full payload
}

/** The resolved full operator payload (SparkByte_Full.json, etc.). */
export interface OperatorPayload {
  identity?: { name?: string; role?: string; description?: string; tags?: string[] };
  behavior?: { core_directives?: string[]; directives?: string[]; avoidances?: string[]; boundaries?: string[] };
  communication_style?: { voice?: string; style_notes?: string[] };
  base_prompt?: string;
  emotion_palette?: Array<{
    name: string;
    valence?: number;
    arousal?: number;
    // Per-emotion sampling nudge, from the MPF palette's sampling_bias. Applied on
    // top of the aperture's base temp/top_p — this is what makes cold operators
    // (Balthazar) actually sample colder than warm ones (SparkByte).
    samplingBias?: { temp?: number; top_p?: number };
  }>;
  llm_profiles?: Record<string, { boot_prompt?: string }>;
  [k: string]: unknown;
}

export interface Telemetry {
  agent: string;
  signals: TurnSignals;
  behaviorState: BehaviorState;
  aperture: ApertureState;
  cognitiveGear: string;
  drift: DriftResponse;
  rhythm: RhythmInfo;
  sampling: { temp: number; top_p: number };
  usedMemoryCount: number;
  hostedBy: "browser" | "server-fallback";
}

export interface TurnResult {
  reply: string;
  telemetry: Telemetry;
}

export interface ChatMessage {
  role: "system" | "user" | "assistant" | "tool";
  content: string;
}
