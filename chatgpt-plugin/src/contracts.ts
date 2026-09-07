import { z } from "zod";

export const PLUGIN_SESSION = "chatgpt-private";
export const WIDGET_URI = "ui://jl-engine/console-v1.html";

export const engineStateSchema = z.object({
  agent: z.string(),
  gait: z.string(),
  rhythmMode: z.string(),
  apertureMode: z.string(),
  emotion: z.string(),
  investmentGear: z.string(),
  stability: z.number(),
  model: z.string(),
});

export const activitySchema = z.object({
  toolName: z.string(),
  succeeded: z.boolean(),
  durationMs: z.number().int().nonnegative(),
});

export const stateOutputSchema = {
  state: engineStateSchema,
  stateVersion: z.number().int().nonnegative(),
};

export const capabilitiesOutputSchema = {
  tools: z.array(z.object({
    name: z.string(),
    description: z.string(),
    dynamic: z.boolean(),
    enabled: z.boolean(),
  })),
  stateVersion: z.number().int().nonnegative(),
};

export const activityOutputSchema = {
  activity: z.array(activitySchema),
  stateVersion: z.number().int().nonnegative(),
};

export const turnInputSchema = {
  message: z.string().trim().min(1).max(20_000),
};

export const turnOutputSchema = {
  reply: z.string(),
  mode: z.enum(["talk", "execute"]),
  state: engineStateSchema,
  toolActivity: z.array(activitySchema),
  stateVersion: z.number().int().nonnegative(),
};

export type EngineState = z.infer<typeof engineStateSchema>;
export type ToolActivity = z.infer<typeof activitySchema>;

export interface TurnResponse {
  reply: string;
  mode: "talk" | "execute";
  state: EngineState;
  toolActivity: ToolActivity[];
}

export interface Capability {
  name: string;
  description: string;
  dynamic: boolean;
  enabled: boolean;
}

export const toolAnnotations = {
  get_engine_state: {
    readOnlyHint: true,
    openWorldHint: false,
    destructiveHint: false,
    idempotentHint: true,
  },
  list_engine_capabilities: {
    readOnlyHint: true,
    openWorldHint: false,
    destructiveHint: false,
    idempotentHint: true,
  },
  get_recent_engine_activity: {
    readOnlyHint: true,
    openWorldHint: false,
    destructiveHint: false,
    idempotentHint: true,
  },
  talk_to_sparkbyte: {
    readOnlyHint: false,
    openWorldHint: false,
    destructiveHint: false,
    idempotentHint: false,
  },
  execute_with_sparkbyte: {
    readOnlyHint: false,
    openWorldHint: true,
    destructiveHint: true,
    idempotentHint: false,
  },
} as const;

export const widgetCsp = {
  connectDomains: [] as string[],
  resourceDomains: [] as string[],
  frameDomains: [] as string[],
};
