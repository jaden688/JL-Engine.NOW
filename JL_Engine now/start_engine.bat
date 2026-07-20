@echo off
setlocal

set "ROOT=%~dp0"
set "EXE=%ROOT%dotnet\JLEngine.Host\bin\Debug\net9.0\JLEngine.Host.exe"

if not exist "%EXE%" (
    echo Build not found. Building JLEngine.Host...
    dotnet build "%ROOT%dotnet\JLEngine.sln" -c Debug
    if errorlevel 1 (
        echo Build failed. See errors above.
        pause
        exit /b 1
    )
)

cd /d "%ROOT%data"

echo Starting JL Engine (chat GUI: http://127.0.0.1:8081/, A2A: http://127.0.0.1:8082/)...
start "" http://127.0.0.1:8081/
"%EXE%"

endlocal
