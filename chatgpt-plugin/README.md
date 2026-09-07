# JL Engine ChatGPT Plugin

This package is the separate MCP Apps adapter for the live `jlnow` JL Engine. It does not contain a replacement engine and does not modify the existing `JL_Engine-SB.Omni` plugin or tunnel profile.

## Capability boundary

- `talk_to_sparkbyte` runs the behavioral engine with executable tools omitted and dispatch blocked for that turn.
- `execute_with_sparkbyte` runs the full autonomous `AgentRuntime` tool loop, including enabled built-in and dynamically forged tools. It is deliberately marked non-read-only, destructive, open-world, and non-idempotent.
- Read tools expose current state, the live capability catalog, and sanitized activity containing only tool name, success status, and duration.

The fixed `chatgpt-private` session calls OpenAI directly with `gpt-5-nano`.
At current list pricing that is $0.05 per million input tokens and $0.40 per
million output tokens. Set the user-level `OPENAI_API_KEY` before starting JL
Engine. You can set `CHATGPT_JL_MODEL` to another OpenAI model ID to trade cost
for capability. Existing GUI and A2A sessions keep their OpenRouter behavior.

## Build and test

```powershell
cd C:\Users\J_lin\Desktop\jlnow\chatgpt-plugin
npm install
npm run build
npm test
```

Start JL Engine separately:

```powershell
& 'C:\Users\J_lin\Desktop\jlnow\JL_Engine now\start_engine.bat'
```

Then run the live adapter smoke test:

```powershell
npm run smoke
```

## Local Streamable HTTP and Inspector

```powershell
npm run start:http
```

The adapter listens on `http://127.0.0.1:3001/mcp`; its health endpoint is `http://127.0.0.1:3001/health`.

In a second terminal:

```powershell
npm run inspect
```

Use MCP Inspector to initialize the server, inspect all five descriptors, read the widget resource, and call the read tools. Only call `execute_with_sparkbyte` with a reversible test request you explicitly approve.

## Dedicated Secure MCP Tunnel

1. Create a new tunnel for `jlnow` at <https://platform.openai.com/settings/organization/tunnels>. Do not reuse the Omni tunnel ID.
2. Copy `tunnel/jlnow.yaml.example` to `C:\Users\J_lin\AppData\Roaming\tunnel-client\jlnow.yaml`.
3. Replace `REPLACE_WITH_NEW_JLNOW_TUNNEL_ID` with the new tunnel ID.
4. Build the adapter, start JL Engine, and run:

```powershell
& 'C:\Users\J_lin\Desktop\jlnow\scripts\start_jlnow_tunnel.ps1'
```

The launcher validates the separate profile, user-level `OPENAI_API_KEY` and
`CONTROL_PLANE_API_KEY`, engine health, and adapter build before starting the
tunnel. It never edits or stops `sparkbyte.yaml`.

In ChatGPT, enable Developer Mode under **Settings → Apps & Connectors → Advanced settings**, add the new tunnel-backed app, and refresh it after any MCP descriptor or widget change.

## Submission boundary

The repository-root `chatgpt-app-submission.json` is a source-grounded private review artifact with five positive and three negative tests. Public directory submission still requires stable production HTTPS hosting, authentication, verified publisher identity, domain verification, public policy/support pages, and a production widget domain.
