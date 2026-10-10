$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "JLEngine.Host\JLEngine.Host.csproj"
$output = Join-Path $root "publish\JLEngine.Host"

Write-Host "Building release publish for JLEngine.Host..."

dotnet publish $project -c Release -o $output --self-contained false

if ($LASTEXITCODE -ne 0) {
	throw "Publish failed."
}

Write-Host "Publish complete: $output"
