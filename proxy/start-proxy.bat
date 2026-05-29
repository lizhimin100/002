@echo off
title ds-proxy
echo [ds-proxy] Starting...
echo [ds-proxy] Listening on http://localhost:16889
echo [ds-proxy] Forwarding to https://api.deepseek.com/anthropic
echo [ds-proxy] Close this window to stop
echo.
cd /d "%~dp0"
ds-proxy.exe
pause
