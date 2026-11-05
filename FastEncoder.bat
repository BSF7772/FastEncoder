@echo off
rem FastEncoder launcher: builds on first run (or after source changes), then starts the app.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\FastEncoder.exe"

if not exist "%EXE%" goto build
if "%~1"=="--rebuild" goto build
goto run

:build
where dotnet >nul 2>&1 || (echo .NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10 & pause & exit /b 1)
echo Building FastEncoder...
dotnet build "%ROOT%src\FastEncoder.csproj" -c Release -nologo -v q
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
if "%~1"=="--rebuild" shift

:run
start "" "%EXE%" %*
endlocal
