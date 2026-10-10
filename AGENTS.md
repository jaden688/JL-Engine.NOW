# AGENTS.md

This file is the canonical source-of-truth for AI coding assistants working in this repository.

## Repository purpose

JL Engine NOW is a .NET 10 local-first AI agent runtime.

Primary architectural intent:
- keep the main engine stable and core
- push behavioral differences into config-driven specialists and profiles
- support local Ollama inference and remote OpenRouter fallback
- keep runtime orchestration separate from core engine logic
- serve a web host/operator dashboard for local use

## Active source directories

Use these as the primary project directories:
- `JLEngine.Core/` - core engine, signal processing, behavior, memory, investment, drift, aperture, MPF, backends
- `JLEngine.Runtime/` - runtime orchestration, tool loop, session composition, chat endpoints
- `JLEngine.Host/` - ASP.NET host and web UI
- `JLEngine.Bridges/` - A2A and external integration layer
- `JLEngine.Persistence/` - persistence and database schema
- `modular_fat_agent_pack/` - config-driven agent profiles and specialist definitions

## Do not treat as canonical

These are not the primary implementation sources unless explicitly requested:
- backup folders
- archived variants
- old experiments
- stale copies of earlier repo state
- generated build folders such as `bin/`, `obj/`, and `publish/`

## Important runtime facts

- The main app host is `JLEngine.Host`
- The runtime composition entry point is `JLEngine.Runtime/RuntimeComposition.cs`
- The engine core entry point is `JLEngine.Core/Engine/JLEngineCore.cs`
- Local Ollama support is implemented in `JLEngine.Core/Backends/OllamaBackend.cs`
- Specialised config-driven agent profiles are loaded via `JLEngine.Core/Mpf/MpfRegistry.cs` and the `modular_fat_agent_pack` folder

## How to validate changes

Run the smallest relevant check:

```powershell
dotnet test .\JLEngine.Core.Tests\JLEngine.Core.Tests.csproj --nologo
```

For host/build packaging validation:

```powershell
dotnet publish .\JLEngine.Host\JLEngine.Host.csproj -c Release -o .\publish\JLEngine.Host --self-contained false --nologo
```

## Operational guidance for AI agents

- Prefer the actual source files over README content when they conflict
- When multiple variants exist, choose the active project directories listed above
- Keep the main engine stable and layer features around it rather than rewriting it
- Preserve config-driven specialist architecture and local backend support
- Prefer surgical changes over broad rewrites
- Do not assume a README or stale note describes the active runtime truth

## Typical entry points

If asked to debug or extend functionality, start here:
1. `JLEngine.Host/Program.cs`
2. `JLEngine.Runtime/RuntimeComposition.cs`
3. `JLEngine.Runtime/AgentRuntime.cs`
4. `JLEngine.Core/Engine/JLEngineCore.cs`
5. `JLEngine.Core/Backends/OllamaBackend.cs`

## Source of truth rule

When there is a conflict between documentation and code, the code in the active project directories wins. README files are for quick onboarding, not authoritative runtime truth.
