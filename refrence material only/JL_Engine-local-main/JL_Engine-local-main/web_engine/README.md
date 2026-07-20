# JL Engine — Browser Core

A browser-native layer of the JL Engine. Two things live here:

1. **The behavioral engine, ported to TypeScript** (`src/`) — a faithful port of
   the Python `engine_core.process_turn` loop:
   `signals → behavior grid → emotional aperture → cognitive gear → drift → rhythm
   → sampling bias → backend`. It loads the **same MPF operator JSON** the Julia and
   Python engines load, so operators are shared across all three hosts.

2. **A Chrome side-panel extension** (`extension/`) — the operator lives *in your real
   browser*, sees the tab you're on, controls it (navigate / click / fill / read /
   tabs), and every action is **observable**: it flashes a marker on any element
   before it touches it, and logs each action to a live activity timeline.

This is the *in-browser* layer. It complements — not replaces — the existing
server-side Playwright driver in `BYTE/src/Tools.jl` (`playwright_interact`), which
drives a separate headless Chromium for heavy autonomous runs. Side panel = co-pilot
in your browser; Playwright = robot browser off to the side. Heavy work hands off to
the server via `ServerFallbackBackend`.

## Run the dev harness (chat + telemetry, no browser control)

```bash
cd web_engine
npm install
npm run dev          # → http://localhost:5173
```

Offline out of the box: bundled SparkByte operator + `EchoBackend`. Watch the live
HUD react to what you type (sentiment, aperture, drift, rhythm, sampling).

## Build & load the Chrome extension (browser control + observation)

```bash
npm run build:ext    # → dist-ext/
```

Then in Chrome: `chrome://extensions` → enable **Developer mode** → **Load unpacked**
→ select `web_engine/dist-ext`. Click the toolbar icon to open the side panel.

**Hands (slash commands):**

| command | does |
|---|---|
| `/go <url>` | navigate the active tab |
| `/read` | read the page into the operator's context |
| `/click <sel or text>` | click a CSS selector or exact link/button text |
| `/fill <sel> :: <value>` | fill a field |
| `/tabs` | list open tabs |

Before every click/fill the target **flashes a labeled outline** on the page, and the
**browser activity** panel logs it with an ok/miss dot — so you always see what it did.

## Going live (real LLM + real operators)

In `extension/sidepanel.ts` (and `src/main.ts`), swap the backend:

```ts
// Local/co-pilot brain — point apiBase at a thin proxy that injects your key:
backend: new OpenRouterBackend({ apiBase: "https://your-proxy/api/llm", model: "anthropic/claude-sonnet-5" }),
```

To load the **real operator roster** instead of the bundled default, serve the Python
engine's data dir and pass:

```ts
new WebEngine({
  operatorSource: { baseUrl: "/operators/", registryFile: "JL_Agents.mpf.json" },
  behaviorConfigUrl: "/operators/behavior_states.json",
})
```

When a live model replies with a tool block, the panel executes it (observed) and
feeds the result back for one follow-up turn:

    ```tool
    { "name": "click", "selector": "button.submit" }
    ```

Tools: `read_page`, `click`, `fill`, `navigate`, `list_tabs`.

## Hybrid: hand heavy runs to the existing server engine

For autonomous multi-step automation, route to the Julia/Playwright engine:

```ts
backend: new ServerFallbackBackend({ url: "http://127.0.0.1:8081/turn", operator: "SparkByte" }),
```

## Layout

```
src/            the ported behavioral engine (host-agnostic)
  signals.ts    conversational_signals.py
  behavior.ts   behavior_engine.py (5×4 grid + triggers)
  aperture.ts   emotional_aperture.py
  drift.ts      drift_pressure.py
  rhythm.ts     rhythm.py
  operators.ts  MPF loader (shared operator spec)
  memory.ts     IndexedDB (browser answer to SQLite hybrid memory)
  backend.ts    OpenRouter / server-fallback / echo
  engine.ts     process_turn, ported
extension/      the Chrome side panel
  page_hands.ts hands + on-page highlighting (observability)
  sidepanel.ts  panel UI, commands, activity timeline
  background.ts MV3 worker (opens panel on icon click)
```
