@echo off
rem ===========================================================================
rem Direct-bot launcher WITH logging (open-issues #4: bot session log restored)
rem
rem Why this exists: C:\Aether\direct-bot\run-direct-{mm,noise}.cmd used to send
rem stdout/stderr to NUL (lesson of 2026-10-29: per-tick logging wrote 8.6GB/day
rem and killed disk I/O). The price was ZERO client-side visibility -- "did it
rem re-login", "why didn't reconnect fire" could only be guessed from the front's
rem logs. Per-tick tracing has been off by default since 2026-10-26
rem (AETHER_CTP_TICK_TRACE), so everyday output is only session events
rem (connect/auth/login/disconnect/rebuild/ready): tens of KB to a few MB/day.
rem
rem This launcher writes stdout/stderr to disk BYTE-FOR-BYTE (cmd's >> does no
rem transcoding, GBK is preserved as-is) with a hard cap:
rem   - one file per day   %INSTANCE%\logs\bot-<yyyyMMdd>.log
rem   - 64MB per file      when exceeded, today's file is renamed to .1 (one
rem                        generation kept)
rem   - result: ~128MB per instance, worst case; no repeat of 8.6GB/day
rem
rem NOTE: this file is deliberately 7-bit ASCII + CRLF. cmd.exe parses .cmd in
rem the OEM codepage (936 on this box), and operator characters can appear as
rem trail bytes inside a multibyte sequence, which splits a "rem" line and turns
rem prose into commands. ASCII removes that entire class of failure. The Chinese
rem documentation lives in the repo, not in this file:
rem   aether-ctp-hosts/doc/direct-bot-bypass.md
rem
rem USAGE: the instance name is passed by the per-instance wrapper through the
rem environment variable INSTANCE, NOT as argv -- cmd's %* is unaffected by
rem shift, so an instance name in %1 would be forwarded to the bot too. See
rem run-direct-mm.cmd / run-direct-noise.cmd in the same directory.
rem ===========================================================================
setlocal enableextensions

if "%INSTANCE%"=="" (
    echo [launcher] missing environment variable INSTANCE ^(instance name, e.g. botnoise^)
    exit /b 2
)

set "ROOT=%~dp0"
set "BOTDIR=%ROOT%%INSTANCE%"
if not exist "%BOTDIR%" (
    echo [launcher] instance directory not found: %BOTDIR%
    exit /b 2
)

set "LOGDIR=%BOTDIR%\logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1

rem Date: %DATE% follows the system locale (yyyy-MM-dd / MM/dd/yyyy / dd.MM.yyyy
rem all exist), so ask PowerShell for the ISO basic form. Otherwise the log file
rem name would change shape with the machine's language and could not be compared
rem across hosts.
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "(Get-Date).ToString('yyyyMMdd')" 2^>nul`) do set "DAY=%%d"
if not defined DAY set "DAY=unknown"

set "LOGFILE=%LOGDIR%\bot-%DAY%.log"

rem 64MB cap: when exceeded, move today's file to .1 (overwriting the previous
rem generation) and keep writing. Deliberately NOT "append forever + clean up
rem later" -- unbounded appending is exactly what killed the disk before.
if exist "%LOGFILE%" for %%F in ("%LOGFILE%") do if %%~zF GEQ 67108864 move /y "%LOGFILE%" "%LOGFILE%.1" >nul 2>&1

rem No operator characters in this echo: an unescaped > here would be parsed as a
rem redirect and truncate the line at that point (caught by the smoke test on
rem 2026-10-30 -- the line stopped at "log -"). Keep parity lines plain.
echo [launcher] %DATE% %TIME% instance %INSTANCE% starting, log file %LOGFILE% >>"%LOGFILE%" 2>&1

rem >> rather than >: several restarts on the same day must accumulate, never
rem truncate the earlier clue.
"%ROOT%publish\Aether.DirectBot.Console.exe" %* >>"%LOGFILE%" 2>&1
set "RC=%ERRORLEVEL%"

echo [launcher] %DATE% %TIME% instance %INSTANCE% exited, exit=%RC% >>"%LOGFILE%" 2>&1
exit /b %RC%
