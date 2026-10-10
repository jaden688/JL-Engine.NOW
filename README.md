# JL Engine NOW

JL Engine NOW is a .NET 10 local-first agent runtime with a layered engine core, runtime orchestration, modular agent profiles, and backend selection for local Ollama and remote OpenRouter usage.

## What this project is

- Core engine with signal scoring, drift, rhythm, investment, and aperture modeling
- Dynamic agent loading via MPF registry and fat-agent profiles
- Local backend support with Ollama
- Remote backend support with OpenRouter
- ASP.NET host for the operator dashboard and local web app
- Persistence and A2A bridge integrations for broader orchestration

## Quick start

### Prerequisites

- .NET 10 SDK
- Optional: Ollama installed and running locally
- Optional: OpenRouter API key if you want remote inference

### Run the host locally

```powershell
dotnet restore
dotnet run --project .\JLEngine.Host\JLEngine.Host.csproj
```

Then open:

- http://localhost:8081

The host is configured to serve the dashboard and accept runtime chat requests.

### Run the core tests

```powershell
dotnet test .\JLEngine.Core.Tests\JLEngine.Core.Tests.csproj --nologo
```

## Ollama setup

If you want local inference:

1. Install Ollama
2. Start Ollama
3. Pull a model, for example:

```powershell
ollama pull qwen3.5:4b
```

4. Ensure the application is pointed at the local backend and the selected model is available.

The app is designed to prefer local Ollama when available, while still supporting remote OpenRouter-based flows.

## Project layout

- `JLEngine.Core` – engine, signals, behavior, memory, MPF, backends
- `JLEngine.Runtime` – runtime orchestration and tool loop
- `JLEngine.Host` – web host + operator UI
- `JLEngine.Bridges` – A2A and bridge integrations
- `JLEngine.Persistence` – database and persistence logic
- `modular_fat_agent_pack` – config-driven specialist profile packs
- `JLEngine.*.Tests` – targeted test coverage

## Release build

```powershell
dotnet publish .\JLEngine.Host\JLEngine.Host.csproj -c Release -o .\publish\JLEngine.Host --self-contained false
```

This produces a portable publish folder suitable for packaging with Inno Setup or similar installers.

## Windows installer (Inno Setup)

A sample installer script is included at:

- `packaging\JLEngineInstaller.iss`

Use Inno Setup 7 to compile it after running the publish step above.

## Notes

- Local backend health matters more than looks. The runtime is most stable when Ollama is installed and the selected model is present.
- The engine remains config-driven, so specialist agents are layered on top of the main core rather than replacing it.
- Build artifacts and local runtime assets are ignored by the repo Git settings.

## License

This repository is provided as-is for local experimentation and engineering development.
