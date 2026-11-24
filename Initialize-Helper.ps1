[CmdletBinding(SupportsShouldProcess)]
param([string]$CorePath)

$ErrorActionPreference = 'Stop'
$helperPath = Join-Path $PSScriptRoot 'mullvad-direct-helper.exe'

if (-not $CorePath) {
    $candidates = @()
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) { $candidates += Join-Path $base 'Clash Verge\verge-mihomo.exe' }
    }
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Clash Verge\verge-mihomo.exe'
    }
    $CorePath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CorePath -or -not (Test-Path -LiteralPath $CorePath -PathType Leaf)) {
    throw 'Installed Clash Verge core not found. Supply -CorePath with its verge-mihomo.exe path.'
}
$sourcePath = (Resolve-Path -LiteralPath $CorePath).Path
if ($sourcePath -eq $helperPath) { throw 'Source and destination must be different files.' }
$running = @(Get-Process -Name 'mullvad-direct-helper' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $helperPath })
if ($running.Count -gt 0) { throw 'The helper in this directory is running. Stop it manually before replacing the binary.' }

if ($PSCmdlet.ShouldProcess($helperPath, 'Copy the installed Clash Verge core as an independent direct helper')) {
    Copy-Item -LiteralPath $sourcePath -Destination $helperPath
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash) {
        throw 'Copied helper hash does not match its source.'
    }
    Write-Output ('Initialized: ' + $helperPath)
    Write-Output 'No process was started; no VPN, proxy, or startup setting was changed.'
}
