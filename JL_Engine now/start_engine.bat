@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%dotnet\JLEngine.Host\JLEngine.Host.csproj"
set "EXE=%ROOT%dotnet\JLEngine.Host\bin\Release\net10.0\JLEngine.Host.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 10 SDK is required. Install it from https://dot.net/download
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo Build not found. Building JL Engine for .NET 10...
    dotnet build "%PROJECT%" -c Release
    if errorlevel 1 (
        echo Build failed. See errors above.
        pause
        exit /b 1
    )
)

if not exist "%ROOT%data" mkdir "%ROOT%data"
cd /d "%ROOT%"

echo Starting JL Engine...
echo Chat GUI: http://127.0.0.1:8081/
echo Press Ctrl+C in this window to stop the engine.
start "" http://127.0.0.1:8081/
"%EXE%"

endlocal
