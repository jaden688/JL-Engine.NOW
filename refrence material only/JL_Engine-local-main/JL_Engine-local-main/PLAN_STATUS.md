# JL Engine — Plan & Status Note

_Written 2026-07-08 by Claude, reconstructing context after a session restart (no memory of the prior conversation carried over — this is based on the user's recap plus inspection of the repo)._

## Stated goal (from user recap)

Extend JL Engine so it becomes **browser-native**, in addition to its current form, and **add support for another coding language** on top of what it already supports.

## What currently exists (confirmed by inspecting the repo)

- **Two separate JL Engine checkouts** live side by side in `comfort/`:
  - `JL_Engine-SB.Omni` — Julia-based engine (`a2a_server.jl`, `a2a_billing.jl`), has its own git history.
  - `JL_Engine-local-main` — Python-based engine (this folder). No git repo in this checkout (looks extracted from a zip).
- `JL_Engine-local-main` is **not** browser-native today: `ui_web/` is a thin JS/HTML client that talks to a local FastAPI backend (`src/jl_platform/services/api/main.py`) — it requires the Python server running locally, it doesn't run the engine standalone in-browser (no WASM/Pyodide, etc.).
- There is a `browser_bridge.py` (`src/jl_platform/core/browser_bridge.py`), but that's browser **automation** (it launches/drives a real Chrome/Edge process) — a different thing from "browser-native engine," worth not confusing with the goal above.
- No code, docs, or scaffolding found yet for either the browser-native port or a second/new language integration — this initiative doesn't appear to have started in this checkout.

## Pre-existing issue noticed along the way (unrelated to the above, but blocking clean startup)

`app.log` shows a recurring warning on every engine load:
```
[AgentValidation] Agent schema file is missing at .../config/agent_schema.json; falling back to baseline validation.
```
`docs/MPF_OPEN_STANDARD.md` says the repo should keep `config/agent_schema.json` and `config/mpf_registry_schema.json`, but the `config/` directory doesn't exist in this checkout at all. Likely worth fixing regardless of the browser/language work, since it's silently degrading validation.

## Open questions to nail down before implementing

1. **"Browser-native"** — does this mean:
   - (a) compiling/porting the Python engine to run fully client-side (e.g. via Pyodide/WASM), or
   - (b) something more modest, like a richer `ui_web/` PWA that still talks to a local/remote API?
2. **"Another coding language"** — which one, and for what purpose:
   - a new implementation language for engine internals (e.g. Rust/TypeScript/Go alongside Python and Julia), or
   - a new *scripting/operator* language exposed to agents/tools (i.e. something MPF-adjacent), or
   - unifying `JL_Engine-SB.Omni` (Julia) and `JL_Engine-local-main` (Python) so one engine can run both?
3. Is this work meant to land in `JL_Engine-local-main`, `JL_Engine-SB.Omni`, or a new unified project?

## Current step

Nothing has been implemented yet for this initiative. This note exists to capture where we are before continuing, since the details of "browser-native" and "the new language" need to be confirmed with the user before writing code.
