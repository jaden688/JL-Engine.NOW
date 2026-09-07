import {
  activitySchema,
  capabilitiesOutputSchema,
  engineStateSchema,
  PLUGIN_SESSION,
  type Capability,
  type EngineState,
  type ToolActivity,
  type TurnResponse,
} from "./contracts.js";
import { z } from "zod";

const DEFAULT_BASE_URL = "http://127.0.0.1:8081";
const DEFAULT_TIMEOUT_MS = 125_000;

export class EngineClientError extends Error {
  constructor(message: string, public readonly status?: number) {
    super(message);
    this.name = "EngineClientError";
  }
}

function boundedError(value: unknown): string {
  const text = value instanceof Error ? value.message : String(value);
  return text.length > 400 ? `${text.slice(0, 400)}…` : text;
}

export class EngineClient {
  constructor(
    private readonly baseUrl = process.env.JL_ENGINE_URL ?? DEFAULT_BASE_URL,
    private readonly timeoutMs = Number(process.env.JL_ENGINE_TIMEOUT_MS ?? DEFAULT_TIMEOUT_MS),
  ) {}

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      const response = await fetch(new URL(path, this.baseUrl), {
        ...init,
        headers: { "content-type": "application/json", ...(init?.headers ?? {}) },
        signal: controller.signal,
      });
      const body = await response.json().catch(() => ({})) as Record<string, unknown>;
      if (!response.ok) {
        throw new EngineClientError(boundedError(body.error ?? `JL Engine returned HTTP ${response.status}.`), response.status);
      }
      return body as T;
    } catch (error) {
      if (error instanceof EngineClientError) throw error;
      if (error instanceof DOMException && error.name === "AbortError") {
        throw new EngineClientError("JL Engine did not finish the turn before the timeout.");
      }
      throw new EngineClientError(`Cannot reach JL Engine at ${this.baseUrl}: ${boundedError(error)}`);
    } finally {
      clearTimeout(timeout);
    }
  }

  async health(): Promise<void> {
    await this.request<{ status: string }>("/health");
  }

  async getState(): Promise<EngineState> {
    const data = await this.request<unknown>(`/api/state?session=${encodeURIComponent(PLUGIN_SESSION)}`);
    return engineStateSchema.parse(data);
  }

  async listCapabilities(): Promise<Capability[]> {
    const data = await this.request<{ tools: unknown }>(`/api/tools?session=${encodeURIComponent(PLUGIN_SESSION)}`);
    return zodCapabilities(data.tools);
  }

  async getRecentActivity(limit = 20): Promise<ToolActivity[]> {
    const data = await this.request<{ activity: unknown }>(
      `/api/chatgpt/activity?session=${encodeURIComponent(PLUGIN_SESSION)}&limit=${Math.min(Math.max(limit, 1), 50)}`,
    );
    return activitySchema.array().parse(data.activity);
  }

  async runTurn(message: string, mode: "talk" | "execute"): Promise<TurnResponse> {
    const data = await this.request<TurnResponse>("/api/chatgpt/turn", {
      method: "POST",
      body: JSON.stringify({ message, mode, sessionId: PLUGIN_SESSION }),
    });
    return {
      reply: String(data.reply ?? ""),
      mode,
      state: engineStateSchema.parse(data.state),
      toolActivity: activitySchema.array().parse(data.toolActivity ?? []),
    };
  }
}

function zodCapabilities(value: unknown): Capability[] {
  return z.object(capabilitiesOutputSchema).shape.tools.parse(value);
}
