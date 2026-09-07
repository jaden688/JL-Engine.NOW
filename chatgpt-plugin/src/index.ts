import { createServer } from "node:http";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { createMcpServer } from "./server.js";

const useHttp = process.argv.includes("--http") || process.env.MCP_TRANSPORT === "http";

if (useHttp) {
  const port = Number(process.env.PORT ?? 3001);
  const host = process.env.HOST ?? "127.0.0.1";
  const httpServer = createServer(async (req, res) => {
    if (!req.url) {
      res.writeHead(400).end("Missing URL");
      return;
    }
    const url = new URL(req.url, `http://${req.headers.host ?? `${host}:${port}`}`);
    if (req.method === "GET" && url.pathname === "/health") {
      res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ status: "ok" }));
      return;
    }
    if (req.method === "OPTIONS" && url.pathname === "/mcp") {
      res.writeHead(204, {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "POST, GET, DELETE, OPTIONS",
        "Access-Control-Allow-Headers": "content-type, mcp-session-id",
        "Access-Control-Expose-Headers": "Mcp-Session-Id",
      }).end();
      return;
    }
    if (url.pathname !== "/mcp" || !req.method || !["POST", "GET", "DELETE"].includes(req.method)) {
      res.writeHead(404, { "content-type": "text/plain" }).end("JL Engine MCP server");
      return;
    }

    res.setHeader("Access-Control-Allow-Origin", "*");
    res.setHeader("Access-Control-Expose-Headers", "Mcp-Session-Id");
    const server = createMcpServer();
    const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined });
    res.on("close", () => {
      void transport.close();
      void server.close();
    });
    try {
      await server.connect(transport);
      await transport.handleRequest(req, res);
    } catch (error) {
      console.error("MCP HTTP request failed:", error);
      if (!res.headersSent) res.writeHead(500);
      if (!res.writableEnded) res.end("MCP request failed");
    }
  });

  httpServer.listen(port, host, () => {
    console.error(`JL Engine MCP listening at http://${host}:${port}/mcp`);
  });
} else {
  const server = createMcpServer();
  await server.connect(new StdioServerTransport());
}
