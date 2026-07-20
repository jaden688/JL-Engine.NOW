// web_engine/src/operators.ts
// MPF operator loader. Reads the SAME registry + payload JSON that the Julia and
// Python engines load. This is the convergence layer: one operator spec, many hosts.
//
// Registry shape (JL_Agents.mpf.json):
//   { "SparkByte": { "jl_agent_file": "fat_agents/SparkByte_Full.json", ... }, ... }
//
// We keep the legacy "jl_agent_file" key on read for compatibility, but expose it
// as `operatorFile` everywhere in the browser engine.

import type { OperatorPayload, OperatorRef } from "./types";
import { BUNDLED_OPERATORS, DEFAULT_OPERATOR } from "./bundled_operators";

export interface OperatorSource {
  /** Base URL the registry + payload files are served from, e.g. "/operators/". */
  baseUrl: string;
  /** Registry filename relative to baseUrl. */
  registryFile?: string;
}

export class OperatorRegistry {
  private refs: Record<string, OperatorRef> = {};
  private cache = new Map<string, OperatorPayload>();
  private src: OperatorSource | null;

  constructor(src?: OperatorSource) {
    this.src = src ?? null;
  }

  async load(): Promise<string[]> {
    if (!this.src) {
      // Offline / zero-config mode: the bundled roster so the engine runs anywhere.
      this.refs = {};
      for (const [name, payload] of Object.entries(BUNDLED_OPERATORS)) {
        this.refs[name] = { agentName: name, classification: "fat_agent", tags: payload.identity?.tags ?? [] };
        this.cache.set(name, payload);
      }
      return Object.keys(this.refs);
    }
    const url = this.src.baseUrl + (this.src.registryFile ?? "JL_Agents.mpf.json");
    const raw = await (await fetch(url)).json();
    this.refs = {};
    for (const [name, entry] of Object.entries<Record<string, unknown>>(raw)) {
      this.refs[name] = {
        agentName: name,
        classification: String(entry.classification ?? "fat_agent"),
        defaultMemoryMode: entry.default_memory_mode as string | undefined,
        defaultBackendId: entry.default_backend_id as string | undefined,
        driveType: (entry.drive_type as string | null) ?? null,
        tags: (entry.tags as string[] | undefined) ?? [],
        // accept either the legacy or the new key
        operatorFile: (entry.operator_file as string) ?? (entry.jl_agent_file as string) ?? undefined,
      };
    }
    return Object.keys(this.refs);
  }

  list(): OperatorRef[] {
    return Object.values(this.refs);
  }

  async resolve(name: string): Promise<OperatorPayload> {
    if (this.cache.has(name)) return this.cache.get(name)!;
    const ref = this.refs[name];
    if (!ref?.operatorFile || !this.src) return DEFAULT_OPERATOR;
    const url = this.src.baseUrl + ref.operatorFile;
    try {
      const payload = (await (await fetch(url)).json()) as OperatorPayload;
      this.cache.set(name, payload);
      return payload;
    } catch {
      return DEFAULT_OPERATOR;
    }
  }
}

/** Build the system boot prompt from a resolved operator payload. */
export function bootPrompt(p: OperatorPayload): string {
  const generic = p.llm_profiles?.generic_llm?.boot_prompt;
  if (generic && generic.trim()) return generic.trim();
  if (p.base_prompt && String(p.base_prompt).trim()) return String(p.base_prompt).trim();

  const id = p.identity ?? {};
  const beh = p.behavior ?? {};
  const comm = p.communication_style ?? {};
  const directives = beh.core_directives ?? beh.directives ?? [];
  const avoid = beh.avoidances ?? beh.boundaries ?? [];
  const lines = [
    id.name ? `You are ${id.name}${id.role ? `, ${id.role}` : ""}.` : "",
    id.description ?? "",
    comm.voice ? `Voice: ${comm.voice}.` : "",
    directives.length ? `Directives:\n- ${directives.join("\n- ")}` : "",
    avoid.length ? `Avoid:\n- ${avoid.join("\n- ")}` : "",
  ].filter(Boolean);
  return lines.join("\n\n");
}
