@echo off
rem Aether DIRECT bot launcher -- logging variant (open-issues #4, 2026-10-30)
rem
rem Deploy path: C:\Aether\direct-bot\run-direct-mm.cmd
rem (replaces the previous ">NUL 2>&1" version; keeps the same instance data dir,
rem  the same exchange endpoint and the same strategy args -- only the stdout
rem  destination changes.)
rem
rem Requires directbot-launcher.cmd in the same directory (scripts/ in this repo).
rem
rem Difference from the old version: stdout/stderr no longer go to NUL, they land in
rem   C:\Aether\direct-bot\botmm\logs\bot-<yyyyMMdd>.log
rem (one file per day, 64MB cap, at most two generations).
rem Per-tick tracing is still off by default (AETHER_CTP_TICK_TRACE), so the volume
rem is session events -- below a few MB/day -- and cannot repeat the 8.6GB/day of
rem 2026-10-29. Liveness is still judged by directbot.heartbeat.json.
rem
rem ASCII + CRLF on purpose, see the header of directbot-launcher.cmd.

setlocal
set "INSTANCE=botmm"
cd /d C:\Aether\direct-bot\botmm
call "%~dp0directbot-launcher.cmd" --profile botmm --exchange 127.0.0.1:17800 --data C:\Aether\direct-bot\botmm --labs C:\Aether\direct-bot\botmm\labs --run-strategy market-maker ax001 --real --max-volume 5
exit /b %ERRORLEVEL%
