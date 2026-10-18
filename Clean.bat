@echo off
rem AI-Ctrl-Agent Clean - dotnet bin/obj, TestResults, run logs.
rem KEEP (never delete): src, tests, web, .github, *.sln, *.props, global.json, *.yaml, *.md, .git
setlocal
set "P=%~dp0"
for /d /r "%P%src" %%d in (bin obj) do @if exist "%%d" rmdir /s /q "%%d" 2>nul
for /d /r "%P%tests" %%d in (bin obj TestResults) do @if exist "%%d" rmdir /s /q "%%d" 2>nul
rmdir /s /q "%P%TestResults" 2>nul
echo CLEAN OK: %P% (bin/obj/TestResults removed, sources kept)
exit /b 0
