$ErrorActionPreference = 'Stop'
$bundleDir = $PSScriptRoot
$helperPath = Join-Path $bundleDir 'mullvad-direct-helper.exe'
$configPath = Join-Path $bundleDir 'direct.yaml'
$runtimeDir = Join-Path $bundleDir 'runtime'
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
$running = @(Get-Process -Name 'mullvad-direct-helper' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $helperPath })
if ($running.Count -gt 0) {
    Write-Output ('Already running. PID: ' + $running[0].Id)
    exit 0
}
$occupied = @(Get-NetTCPConnection -LocalPort 18997 -State Listen -ErrorAction SilentlyContinue)
if ($occupied.Count -gt 0) { throw 'Port 18997 is already in use. No process was changed.' }
$arguments = '-d "{0}" -f "{1}"' -f $runtimeDir, $configPath
$helperProcess = Start-Process -FilePath $helperPath -ArgumentList $arguments -WorkingDirectory $bundleDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeDir 'helper.log') -RedirectStandardError (Join-Path $runtimeDir 'helper-error.log') -PassThru
Set-Content -LiteralPath (Join-Path $runtimeDir 'helper.pid') -Value $helperProcess.Id -Encoding ASCII
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 100
    if ($helperProcess.HasExited) { throw 'Helper exited. Check runtime\helper.log and helper-error.log.' }
    $listener = @(Get-NetTCPConnection -LocalPort 18997 -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $helperProcess.Id })
    if ($listener.Count -gt 0) { $ready = $true; break }
}
if (-not $ready) {
    Stop-Process -Id $helperProcess.Id -ErrorAction SilentlyContinue
    throw 'Helper did not become ready and was stopped.'
}
Write-Output ('Started on 127.0.0.1:18997. PID: ' + $helperProcess.Id)
Write-Output 'Keep this executable excluded in the selected VPN client. No VPN or Clash setting was changed.'
