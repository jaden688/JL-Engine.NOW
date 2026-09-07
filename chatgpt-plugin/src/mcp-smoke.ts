import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

const client = new Client({ name: "jl-engine-smoke", version: "0.1.0" });
const transport = new StreamableHTTPClientTransport(new URL(process.env.JL_MCP_URL ?? "http://127.0.0.1:3001/mcp"));

try {
  await client.connect(transport);
  const tools = await client.listTools();
  const resources = await client.listResources();
  const widget = await client.readResource({ uri: "ui://jl-engine/console-v1.html" });
  const state = await client.callTool({ name: "get_engine_state", arguments: {} });
  console.log(JSON.stringify({
    ok: true,
    tools: tools.tools.map((tool) => tool.name),
    resources: resources.resources.map((resource) => resource.uri),
    widgetMimeType: widget.contents[0]?.mimeType,
    stateHasStructuredContent: Boolean(state.structuredContent),
  }, null, 2));
} finally {
  await client.close();
}
