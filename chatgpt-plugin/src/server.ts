import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import {
  registerAppResource,
  registerAppTool,
  RESOURCE_MIME_TYPE,
} from "@modelcontextprotocol/ext-apps/server";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import {
  activityOutputSchema,
  capabilitiesOutputSchema,
  stateOutputSchema,
  toolAnnotations,
  turnInputSchema,
  turnOutputSchema,
  WIDGET_URI,
  widgetCsp,
} from "./contracts.js";
import { EngineClient } from "./engine-client.js";

const widgetPath = fileURLToPath(new URL("../public/console.html", import.meta.url));
const widgetHtml = readFileSync(widgetPath, "utf8");

const widgetToolMeta = {
  ui: { resourceUri: WIDGET_URI },
  "openai/outputTemplate": WIDGET_URI,
} as const;

export const toolContracts = {
  get_engine_state: {
    title: "Get JL Engine state",
    description: "Use this when the user wants to inspect SparkByte's current agent, gait, rhythm, aperture, emotion, stability, or model without running an engine turn.",
    inputSchema: {},
    outputSchema: stateOutputSchema,
    annotations: toolAnnotations.get_engine_state,
  },
  list_engine_capabilities: {
    title: "List JL Engine capabilities",
    description: "Use this when the user wants to see which built-in and dynamically forged tools are currently available to SparkByte.",
    inputSchema: {},
    outputSchema: capabilitiesOutputSchema,
    annotations: toolAnnotations.list_engine_capabilities,
  },
  get_recent_engine_activity: {
    title: "Get recent JL Engine activity",
    description: "Use this when the user wants a sanitized list of tools SparkByte recently invoked, including success status and duration but not arguments or raw results.",
    inputSchema: { limit: z.number().int().min(1).max(50).optional() },
    outputSchema: activityOutputSchema,
    annotations: toolAnnotations.get_recent_engine_activity,
  },
  talk_to_sparkbyte: {
    title: "Talk to SparkByte",
    description: "Use this for ordinary stateful conversation with SparkByte. This mode runs JL Engine's behavioral stack but advertises and permits no executable engine tools.",
    inputSchema: turnInputSchema,
    outputSchema: turnOutputSchema,
    annotations: toolAnnotations.talk_to_sparkbyte,
    _meta: {
      ...widgetToolMeta,
      "openai/toolInvocation/invoking": "Talking to SparkByte…",
      "openai/toolInvocation/invoked": "SparkByte replied.",
    },
  },
  execute_with_sparkbyte: {
    title: "Execute with SparkByte",
    description: "Use this only when the user explicitly asks SparkByte to take action. It starts a full autonomous JL Engine turn and may write or overwrite files, execute shell commands or code, forge tools, control a browser, send external messages, or publish content. The action is non-idempotent and may be difficult to reverse.",
    inputSchema: turnInputSchema,
    outputSchema: turnOutputSchema,
    annotations: toolAnnotations.execute_with_sparkbyte,
    _meta: {
      ...widgetToolMeta,
      "openai/toolInvocation/invoking": "SparkByte is executing…",
      "openai/toolInvocation/invoked": "SparkByte finished the execution turn.",
    },
  },
} as const;

function versionToken(): number {
  return Date.now();
}

export function createMcpServer(client = new EngineClient()): McpServer {
  const server = new McpServer(
    { name: "jl-engine", version: "0.1.0" },
    {
      instructions:
        "Use talk_to_sparkbyte for ordinary conversation. Use execute_with_sparkbyte only after the user explicitly asks JL Engine to act and approves the consequential turn. Never imply that talk mode can execute tools. Read tools are safe for inspection.",
    },
  );

  registerAppResource(server, "jl-engine-console", WIDGET_URI, {}, async () => ({
    contents: [{
      uri: WIDGET_URI,
      mimeType: RESOURCE_MIME_TYPE,
      text: widgetHtml,
      _meta: {
        ui: {
          prefersBorder: true,
          csp: widgetCsp,
        },
        "openai/widgetDescription": "A compact JL Engine console showing SparkByte replies, cognitive state, and sanitized tool activity.",
        "openai/widgetPrefersBorder": true,
      },
    }],
  }));

  server.registerTool("get_engine_state", toolContracts.get_engine_state, async () => {
    const state = await client.getState();
    return {
      structuredContent: { state, stateVersion: versionToken() },
      content: [{ type: "text" as const, text: `${state.agent}: ${state.gait} gait, ${state.rhythmMode} rhythm, stability ${state.stability}.` }],
    };
  });

  server.registerTool("list_engine_capabilities", toolContracts.list_engine_capabilities, async () => {
    const tools = await client.listCapabilities();
    return {
      structuredContent: { tools, stateVersion: versionToken() },
      content: [{ type: "text" as const, text: `JL Engine currently exposes ${tools.length} enabled and disabled capabilities.` }],
    };
  });

  server.registerTool("get_recent_engine_activity", toolContracts.get_recent_engine_activity, async ({ limit }) => {
    const activity = await client.getRecentActivity(limit ?? 20);
    return {
      structuredContent: { activity, stateVersion: versionToken() },
      content: [{ type: "text" as const, text: `Returned ${activity.length} sanitized JL Engine activity records.` }],
    };
  });

  registerAppTool(server, "talk_to_sparkbyte", toolContracts.talk_to_sparkbyte, async ({ message }) => {
    const result = await client.runTurn(message, "talk");
    return {
      structuredContent: { ...result, stateVersion: versionToken() },
      content: [{ type: "text" as const, text: result.reply }],
      _meta: { presentation: "jl-engine-console", activityDetailsRedacted: true },
    };
  });

  registerAppTool(server, "execute_with_sparkbyte", toolContracts.execute_with_sparkbyte, async ({ message }) => {
    const result = await client.runTurn(message, "execute");
    return {
      structuredContent: { ...result, stateVersion: versionToken() },
      content: [{ type: "text" as const, text: result.reply }],
      _meta: { presentation: "jl-engine-console", activityDetailsRedacted: true },
    };
  });

  return server;
}
