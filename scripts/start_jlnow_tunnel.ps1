$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot "chatgpt-plugin"
$adapterEntry = Join-Path $pluginRoot "dist\index.js"
$profileDir = Join-Path $env:APPDATA "tunnel-client"
$profilePath = Join-Path $profileDir "jlnow.yaml"
$defaultTunnelClient = "C:\Users\J_lin\Desktop\JL_Engine-SB.Omni\tools\tunnel-client\tunnel-client.exe"
$tunnelClient = if ($env:TUNNEL_CLIENT_EXE) { $env:TUNNEL_CLIENT_EXE } else { $defaultTunnelClient }

if (-not (Test-Path -LiteralPath $tunnelClient)) {
    throw "tunnel-client.exe was not found. Set TUNNEL_CLIENT_EXE to its full path."
}
if (-not (Test-Path -LiteralPath $adapterEntry)) {
    throw "The MCP adapter is not built. Run 'npm install' and 'npm run build' in $pluginRoot."
}
if (-not (Test-Path -LiteralPath $profilePath)) {
    throw "The dedicated jlnow tunnel profile is missing at $profilePath. Copy chatgpt-plugin\tunnel\jlnow.yaml.example there and insert a new JLNOW tunnel ID."
}

$profileText = Get-Content -Raw -LiteralPath $profilePath
if ($profileText.Contains("REPLACE_WITH_NEW_JLNOW_TUNNEL_ID")) {
    throw "The jlnow tunnel profile still contains the placeholder tunnel ID. Create a separate tunnel at https://platform.openai.com/settings/organization/tunnels and update $profilePath."
}

$controlPlaneKey = [Environment]::GetEnvironmentVariable("CONTROL_PLANE_API_KEY", "User")
$openAiKey = [Environment]::GetEnvironmentVariable("OPENAI_API_KEY", "User")
if ([string]::IsNullOrWhiteSpace($controlPlaneKey)) {
    throw "CONTROL_PLANE_API_KEY is not stored in the current user's environment."
}
if ([string]::IsNullOrWhiteSpace($openAiKey)) {
    throw "OPENAI_API_KEY is not stored in the current user's environment. The private ChatGPT session uses direct OpenAI with gpt-5-nano."
}
$env:CONTROL_PLANE_API_KEY = $controlPlaneKey
$env:OPENAI_API_KEY = $openAiKey

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:8081/health" -TimeoutSec 4
    if ($health.status -ne "ok") { throw "unexpected engine health response" }
}
catch {
    throw "JL Engine is not healthy on http://127.0.0.1:8081. Start JL_Engine now\start_engine.bat first. $($_.Exception.Message)"
}

& $tunnelClient doctor --profile-dir $profileDir --profile jlnow
if ($LASTEXITCODE -ne 0) { throw "jlnow tunnel profile validation failed with exit code $LASTEXITCODE." }

& $tunnelClient run --profile-dir $profileDir --profile jlnow
if ($LASTEXITCODE -ne 0) { throw "jlnow tunnel startup failed with exit code $LASTEXITCODE." }
