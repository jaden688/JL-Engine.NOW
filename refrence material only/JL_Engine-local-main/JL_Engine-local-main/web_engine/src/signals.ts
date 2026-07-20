// web_engine/src/signals.ts
// Port of conversational_signals.py — heuristic per-turn scoring, no external model.

import type { TurnSignals } from "./types";

const POS = new Set([
  "great","awesome","thanks","good","fantastic","excellent","happy","joy","wonderful","brilliant",
  "support","clarity","help","solve","guide","create","build","innovate","progress","success","win",
  "improve","calm","relaxed","relief","confident","thankful","appreciate","grateful","team","collaborate",
  "energized","motivated","inspired","bright","spark","positive","optimistic","steady","glad","hopeful",
  "focus","clarify","achieve","resolve","empower","assist","love","nice","cool","perfect","yes",
]);

const NEG = new Set([
  "bad","hate","angry","annoyed","frustrated","upset","broken","issue","problem","confused","lost",
  "stuck","sad","terrible","awful","worst","fail","error","panic","worry","anxiety","fear","hurt",
  "tired","exhausted","depressed","miserable","scared","danger","crash","stop","delay","weak","stress",
  "tension","dread","overwhelmed","rude","hostile","suck","no","wrong","ugh","fading",
]);

const DIRECTIVE_PHRASES = [
  "be concise","keep it short","tl;dr","just tell me","quickly","in short","brief","one line",
  "no fluff","get to the point","short answer","asap","just do it","fix it",
];

const CONFUSION_MARKERS = ["?","huh","what do you mean","i don't get","idk","unclear","confused","how do i","not sure"];

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}

export function scoreSignals(text: string): TurnSignals {
  const raw = (text || "").trim();
  const lower = raw.toLowerCase();
  const words = lower.split(/\s+/).filter(Boolean);
  const n = words.length || 1;

  let pos = 0, neg = 0;
  for (const w of words) {
    const clean = w.replace(/[^a-z']/g, "");
    if (POS.has(clean)) pos++;
    if (NEG.has(clean)) neg++;
  }
  const sentiment = clamp((pos - neg) / Math.sqrt(n), -1, 1);

  const exclaims = (raw.match(/!/g) || []).length;
  const caps = (raw.match(/[A-Z]/g) || []).length;
  const capsRatio = raw.length ? caps / raw.length : 0;
  const arousal = clamp(exclaims * 0.18 + capsRatio * 1.4 + (pos + neg) / n, 0, 1);

  const directive =
    DIRECTIVE_PHRASES.some((p) => lower.includes(p)) || (n <= 5 && /^(fix|do|make|run|stop|show|list|add)/.test(lower));

  const questionMarks = (raw.match(/\?/g) || []).length;
  const confusionHits = CONFUSION_MARKERS.reduce((acc, m) => acc + (lower.includes(m) ? 1 : 0), 0);
  const confusion = clamp(questionMarks * 0.2 + confusionHits * 0.25, 0, 1);

  // Short, punchy, exclaimy → fast pace. Long, comma-heavy → slow.
  const avgLen = raw.length / n;
  const pace = clamp(0.5 + exclaims * 0.1 - (avgLen - 5) * 0.03 - questionMarks * 0.05, 0, 1);

  // Longer messages and named entities imply more memory pressure.
  const memoryDensity = clamp(Math.log2(n + 1) / 8 + (raw.match(/[A-Z][a-z]{2,}/g) || []).length * 0.04, 0, 1);

  return { sentiment, arousal, directive, confusion, pace, memoryDensity };
}

/** Coarse trigger inference, mirroring _derive_trigger_from_signals. */
export function deriveTrigger(s: TurnSignals): string {
  if (s.confusion > 0.5 || (s.sentiment < -0.3 && s.arousal > 0.5)) return "user_overwhelmed";
  if (s.arousal > 0.6 && s.sentiment > 0.2) return "user_hyped";
  if (s.sentiment > 0.3 && s.pace > 0.5) return "user_joking";
  if (s.sentiment < -0.1 || s.pace < 0.35) return "user_serious_or_tired";
  return "user_engaged";
}
