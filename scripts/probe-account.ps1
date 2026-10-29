param([string]$User = 'botmm')
# Read-only account/book probe through the CTP front (:17801/:17802).
# Password comes from the instance config -- never passed on a command line, never printed.
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$cfgPath = "C:\Aether\direct-bot\$User\config.json"
if (-not (Test-Path $cfgPath)) { throw "no config: $cfgPath" }
$cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$prof = $cfg.profiles.($cfg.defaultProfile)
$pass = $prof.password
if (-not $pass) { throw "config has no password" }

$exe = 'C:\Aether\ctp-console\publish\Aether.CtpConsole.exe'
$data = "C:\Aether\ctp-console\probe-$User"
New-Item -ItemType Directory -Force $data | Out-Null

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "--front tcp://127.0.0.1:17801 --md-front tcp://127.0.0.1:17802 --user $User --pass $pass --data $data"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)

$p.StandardInput.WriteLine('sub ax001')
Start-Sleep -Seconds 6
$p.StandardInput.WriteLine('tick ax001')
$p.StandardInput.WriteLine('account')
$p.StandardInput.WriteLine('positions')
$p.StandardInput.WriteLine('orders')
Start-Sleep -Seconds 8
$p.StandardInput.WriteLine('quit')
if (-not $p.WaitForExit(20000)) { $p.Kill() }
$out = $p.StandardOutput.ReadToEnd()
[System.IO.File]::WriteAllText('C:\Aether\build-tools\probe-out.txt', $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes('C:\Aether\build-tools\probe-out.txt')))
