import { afterEach, describe, expect, it, vi } from "vitest";
import { EngineClient, EngineClientError } from "./engine-client.js";

const state = {
  agent: "SparkByte",
  gait: "steady",
  rhythmMode: "flow",
  apertureMode: "focused",
  emotion: "curious",
  investmentGear: "engaged",
  stability: 0.92,
  model: "test/model",
};

afterEach(() => vi.unstubAllGlobals());

describe("EngineClient", () => {
  it("uses the fixed private session and explicit execution mode", async () => {
    const fetchMock = vi.fn(async (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body));
      expect(body).toMatchObject({ sessionId: "chatgpt-private", mode: "execute", message: "do it" });
      return new Response(JSON.stringify({ reply: "done", mode: "execute", state, toolActivity: [] }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    });
    vi.stubGlobal("fetch", fetchMock);

    const result = await new EngineClient("http://127.0.0.1:8081").runTurn("do it", "execute");

    expect(result.reply).toBe("done");
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("returns a bounded connection error without leaking response details", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new Error("x".repeat(2_000));
    }));

    await expect(new EngineClient("http://127.0.0.1:65530").getState())
      .rejects.toSatisfy((error: unknown) => error instanceof EngineClientError && error.message.length < 500);
  });
});
