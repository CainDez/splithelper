$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$parseErrors = @()
Get-ChildItem -LiteralPath $repo -Filter '*.ps1' -Recurse | ForEach-Object {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    $parseErrors += @($errors)
}
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sandbox = Join-Path $temporaryRoot ('mullvad-portable-test-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $sandbox 'folder with spaces'
$listener = $null
$originalHelpers = @(Get-CimInstance Win32_Process -Filter "Name = 'mullvad-direct-helper.exe'" | Select-Object -ExpandProperty ProcessId)
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    foreach ($name in @('Initialize-Helper.ps1','Start-Helper.ps1','Stop-Helper.ps1','Enable-Autostart.ps1','direct.yaml')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $fixture $name)
    }
    $source = Join-Path $sandbox 'fake-core.exe'
    [IO.File]::WriteAllText($source, 'synthetic copy fixture; never executed')
    & (Join-Path $fixture 'Initialize-Helper.ps1') -CorePath $source | Out-Null
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath (Join-Path $fixture 'mullvad-direct-helper.exe')).Hash) {
        throw 'Initialization did not preserve source bytes.'
    }
    & (Join-Path $fixture 'Stop-Helper.ps1') | Out-Null
    $occupied = @(Get-NetTCPConnection -LocalPort 18997 -State Listen -ErrorAction SilentlyContinue)
    if ($occupied.Count -eq 0) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 18997)
        $listener.Start()
    }
    $refusedOccupiedPort = $false
    try { & (Join-Path $fixture 'Start-Helper.ps1') | Out-Null } catch { $refusedOccupiedPort = $_.Exception.Message -match 'already in use' }
    if (-not $refusedOccupiedPort) { throw 'Start must refuse an occupied port before launching any binary.' }
    $currentHelpers = @(Get-CimInstance Win32_Process -Filter "Name = 'mullvad-direct-helper.exe'" | Select-Object -ExpandProperty ProcessId)
    $originalIDs = ($originalHelpers | Sort-Object) -join ','
    $currentIDs = ($currentHelpers | Sort-Object) -join ','
    if ($originalIDs -ne $currentIDs) {
        throw 'Tests changed an existing helper process.'
    }
    if ($originalHelpers.Count -eq 1) {
        $live = Get-CimInstance Win32_Process -Filter "ProcessId = $($originalHelpers[0])"
        $plan = & (Join-Path $repo 'Enable-Autostart.ps1') -WhatIf
        if ($plan.HelperDirectory -ne (Split-Path -Parent $live.ExecutablePath)) {
            throw 'Autostart did not use the currently running helper directory.'
        }
        $refusedWrongDirectory = $false
        try { & (Join-Path $fixture 'Enable-Autostart.ps1') -HelperDirectory $fixture -WhatIf | Out-Null } catch {
            $refusedWrongDirectory = $_.Exception.Message -match 'running helper'
        }
        if (-not $refusedWrongDirectory) { throw 'Autostart accepted a directory different from the running helper.' }
    } elseif ($originalHelpers.Count -eq 0) {
        $plan = & (Join-Path $fixture 'Enable-Autostart.ps1') -HelperDirectory $fixture -WhatIf
        if ($plan.HelperDirectory -ne $fixture) { throw 'Explicit fresh-machine directory was not preserved.' }
    }
    & {
        # Simulate a fresh machine without stopping any real helper process.
        function Get-CimInstance { param([string]$ClassName, [string]$Filter) @() }
        $freshPlan = & (Join-Path $fixture 'Enable-Autostart.ps1') -WhatIf
        if ($freshPlan.HelperDirectory -ne $fixture) { throw 'Fresh-machine fallback did not use the script directory.' }
    }
    & {
        # Exercise the real ownership branch against an in-memory unrelated shortcut.
        $fakeStartupPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Mullvad Domestic Direct Helper.lnk'
        $otherDirectory = Join-Path $sandbox 'unrelated project'
        $otherArguments = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}"' -f (Join-Path $otherDirectory 'Start-Helper.ps1')
        $fakeShortcut = [PSCustomObject]@{
            TargetPath=(Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
            Arguments=$otherArguments
            WorkingDirectory=$otherDirectory
            Description='An unrelated project with the same script filename'
            Saves=0
        }
        $fakeShortcut | Add-Member -MemberType ScriptMethod -Name Save -Value { $this.Saves++ }
        $fakeShell = [PSCustomObject]@{Existing=$fakeShortcut}
        $fakeShell | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { param($path) $this.Existing }
        function Get-CimInstance { param([string]$ClassName, [string]$Filter) @() }
        function Test-Path {
            param([string]$LiteralPath, [string]$PathType)
            if ($LiteralPath -eq $fakeStartupPath) { return $true }
            if ($PathType) { return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath -PathType $PathType }
            return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
        }
        function Get-ItemProperty { [CmdletBinding()] param([string]$LiteralPath, [string]$Name) $null }
        function New-Object { param([string]$ComObject) $fakeShell }
        function New-Item { throw 'Unexpected filesystem mutation in ownership check.' }
        function Copy-Item { throw 'Unexpected startup backup in ownership check.' }
        $refusedUnrelated = $false
        try { & (Join-Path $fixture 'Enable-Autostart.ps1') -HelperDirectory $fixture -Confirm:$false | Out-Null } catch {
            $refusedUnrelated = $_.Exception.Message -match 'A different startup command'
        }
        if (-not $refusedUnrelated -or $fakeShortcut.Saves -ne 0 -or $fakeShortcut.Arguments -ne $otherArguments) {
            throw 'An unrelated same-filename startup shortcut was not preserved.'
        }
    }
    Write-Output 'Portable checks passed: parsing, spaced paths, byte-preserving initialization, port conflict, process isolation, autostart dry run, and unrelated-startup preservation.'
} finally {
    if ($null -ne $listener) { $listener.Stop() }
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolvedSandbox.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedSandbox).StartsWith('mullvad-portable-test-')) {
        throw 'Unexpected temporary cleanup target; preserved for inspection.'
    }
    if (Test-Path -LiteralPath $resolvedSandbox) { Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force }
}
