import { EngineClient } from "./engine-client.js";

const client = new EngineClient();
await client.health();
const [state, tools, activity] = await Promise.all([
  client.getState(),
  client.listCapabilities(),
  client.getRecentActivity(5),
]);
console.log(JSON.stringify({ ok: true, agent: state.agent, toolCount: tools.length, recentActivity: activity.length }, null, 2));
