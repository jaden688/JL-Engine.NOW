// web_engine/src/backend.ts
// LLM backends for the browser engine.
//
//  - OpenRouterBackend: calls an OpenAI-compatible chat endpoint. In production
//    the `apiBase` should point at a THIN PROXY you host, so the API key never
//    ships to the tab. For local dev you can point straight at OpenRouter.
//  - ServerFallbackBackend: the hybrid escape hatch. For anything the browser
//    can't do (shell, filesystem, forge, A2A), POST the turn up to the existing
//    Julia/Python engine and use its reply.
//  - EchoBackend: offline stub so the loop is testable with no network at all.

import type { ChatMessage } from "./types";

export interface Backend {
  readonly name: string;
  generate(messages: ChatMessage[], opts: { temperature: number; top_p: number }): Promise<string>;
}

export class OpenRouterBackend implements Backend {
  readonly name = "openrouter";
  constructor(
    private cfg: {
      apiBase: string;          // e.g. "/api/llm" (your proxy) or "https://openrouter.ai/api/v1"
      model: string;
      apiKey?: string;          // omit in prod; the proxy injects it
    },
  ) {}

  async generate(messages: ChatMessage[], opts: { temperature: number; top_p: number }): Promise<string> {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (this.cfg.apiKey) headers["Authorization"] = `Bearer ${this.cfg.apiKey}`;
    const res = await fetch(`${this.cfg.apiBase}/chat/completions`, {
      method: "POST",
      headers,
      body: JSON.stringify({
        model: this.cfg.model,
        messages,
        temperature: opts.temperature,
        top_p: opts.top_p,
      }),
    });
    if (!res.ok) throw new Error(`LLM backend ${res.status}: ${await res.text()}`);
    const data = await res.json();
    return data.choices?.[0]?.message?.content ?? "";
  }
}

export class ServerFallbackBackend implements Backend {
  readonly name = "server-fallback";
  constructor(private cfg: { url: string; operator: string }) {}

  async generate(messages: ChatMessage[]): Promise<string> {
    // Hand the raw turn to the heavy server engine (Julia/Python) and take its reply.
    const lastUser = [...messages].reverse().find((m) => m.role === "user");
    const res = await fetch(this.cfg.url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ agent_name: this.cfg.operator, message: lastUser?.content ?? "" }),
    });
    if (!res.ok) throw new Error(`Server fallback ${res.status}`);
    const data = await res.json();
    return data.reply ?? data.output ?? "";
  }
}

export class EchoBackend implements Backend {
  readonly name = "echo";
  async generate(messages: ChatMessage[], opts: { temperature: number; top_p: number }): Promise<string> {
    const lastUser = [...messages].reverse().find((m) => m.role === "user");
    return `[echo · temp=${opts.temperature.toFixed(2)} top_p=${opts.top_p.toFixed(2)}] ${lastUser?.content ?? ""}`;
  }
}
