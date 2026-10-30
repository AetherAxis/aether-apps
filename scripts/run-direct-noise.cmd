@echo off
rem Aether DIRECT bot launcher -- logging variant (open-issues #4, 2026-10-30)
rem
rem Deploy path: C:\Aether\direct-bot\run-direct-noise.cmd
rem
rem Requires directbot-launcher.cmd in the same directory (scripts/ in this repo).
rem Difference from the old version: stdout/stderr no longer go to NUL, they land in
rem   C:\Aether\direct-bot\botnoise\logs\bot-<yyyyMMdd>.log
rem (one file per day, 64MB cap, at most two generations). Volume stays in the
rem session-event range because per-tick tracing is off by default.
rem
rem ASCII + CRLF on purpose, see the header of directbot-launcher.cmd.

setlocal
set "INSTANCE=botnoise"
cd /d C:\Aether\direct-bot\botnoise
call "%~dp0directbot-launcher.cmd" --profile botnoise --exchange 127.0.0.1:17800 --data C:\Aether\direct-bot\botnoise --labs C:\Aether\direct-bot\botnoise\labs --run-strategy noise-taker ax001 --real --max-volume 5
exit /b %ERRORLEVEL%
