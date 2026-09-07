import { describe, expect, it } from "vitest";
import { toolAnnotations, widgetCsp } from "./contracts.js";
import { toolContracts } from "./server.js";

describe("JL Engine MCP contracts", () => {
  it("exposes exactly the five planned tools with output schemas", () => {
    expect(Object.keys(toolContracts)).toEqual([
      "get_engine_state",
      "list_engine_capabilities",
      "get_recent_engine_activity",
      "talk_to_sparkbyte",
      "execute_with_sparkbyte",
    ]);
    for (const contract of Object.values(toolContracts)) {
      expect(contract.outputSchema).toBeDefined();
      expect(contract.annotations).toBeDefined();
    }
  });

  it("classifies full execution conservatively", () => {
    expect(toolAnnotations.execute_with_sparkbyte).toMatchObject({
      readOnlyHint: false,
      openWorldHint: true,
      destructiveHint: true,
      idempotentHint: false,
    });
  });

  it("keeps talk stateful but without destructive or open-world labels", () => {
    expect(toolAnnotations.talk_to_sparkbyte).toMatchObject({
      readOnlyHint: false,
      openWorldHint: false,
      destructiveHint: false,
    });
  });

  it("uses a zero-domain widget CSP", () => {
    expect(widgetCsp).toEqual({ connectDomains: [], resourceDomains: [], frameDomains: [] });
  });
});
