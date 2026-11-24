$ErrorActionPreference = 'Stop'
$helperPath = Join-Path $PSScriptRoot 'mullvad-direct-helper.exe'
$running = @(Get-Process -Name 'mullvad-direct-helper' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $helperPath })
foreach ($helperProcess in $running) { Stop-Process -Id $helperProcess.Id }
Write-Output ('Stopped matching helper processes: ' + $running.Count)
Write-Output 'No VPN or Clash setting was changed.'
