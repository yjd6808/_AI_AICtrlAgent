@echo off
rem AI-Ctrl-Agent Clean - dotnet bin/obj, TestResults, web node_modules/dist.
rem KEEP (never delete): src, tests, web source, .github, *.sln, *.props, global.json, *.yaml, *.md, .git
setlocal
set "P=%~dp0"
for /d /r "%P%src" %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d" 2>nul
for /d /r "%P%tests" %%d in (bin obj TestResults) do @if exist "%%d" rmdir /s /q "%%d" 2>nul
rmdir /s /q "%P%TestResults" 2>nul
if exist "%P%web\ai-ctrl-web\node_modules" rmdir /s /q "%P%web\ai-ctrl-web\node_modules" 2>nul
if exist "%P%web\ai-ctrl-web\dist" rmdir /s /q "%P%web\ai-ctrl-web\dist" 2>nul
if exist "%P%web\ai-ctrl-web\test-results" rmdir /s /q "%P%web\ai-ctrl-web\test-results" 2>nul
if exist "%P%web\ai-ctrl-web\playwright-report" rmdir /s /q "%P%web\ai-ctrl-web\playwright-report" 2>nul
if exist "%P%web\ai-ctrl-web\blob-report" rmdir /s /q "%P%web\ai-ctrl-web\blob-report" 2>nul
if exist "%P%web\ai-ctrl-web\playwright\.cache" rmdir /s /q "%P%web\ai-ctrl-web\playwright\.cache" 2>nul
echo CLEAN OK: %P% (bin/obj/TestResults/node_modules/dist/playwright artifacts removed, sources kept)
exit /b 0
