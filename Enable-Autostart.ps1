[CmdletBinding(SupportsShouldProcess)]
param([string]$HelperDirectory)

$ErrorActionPreference = 'Stop'
$instances = @(Get-CimInstance Win32_Process -Filter "Name = 'mullvad-direct-helper.exe'")
$runningDirectories = @()
foreach ($instance in $instances) {
    if (-not $instance.ExecutablePath) {
        throw 'Cannot read a running helper path. Preserve the existing startup item and inspect that instance first.'
    }
    $runningDirectories += Split-Path -Parent $instance.ExecutablePath
}
$runningDirectories = @($runningDirectories | Sort-Object -Unique)
if ($runningDirectories.Count -gt 1) { throw 'Helpers are running from multiple directories. Resolve the ambiguity before configuring autostart.' }
if ($runningDirectories.Count -eq 1) {
    $liveDirectory = [IO.Path]::GetFullPath($runningDirectories[0]).TrimEnd('\')
    if ($HelperDirectory -and [IO.Path]::GetFullPath($HelperDirectory).TrimEnd('\') -ne $liveDirectory) {
        throw 'The requested directory differs from the running helper. The running directory is authoritative.'
    }
    $HelperDirectory = $liveDirectory
} elseif (-not $HelperDirectory) {
    $HelperDirectory = $PSScriptRoot
}
$HelperDirectory = (Resolve-Path -LiteralPath $HelperDirectory).Path.TrimEnd('\')
$scriptPath = Join-Path $HelperDirectory 'Start-Helper.ps1'
foreach ($name in @('Start-Helper.ps1', 'direct.yaml', 'mullvad-direct-helper.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $HelperDirectory $name) -PathType Leaf)) {
        throw ('Required file missing from helper directory: ' + $name)
    }
}
$startupDirectory = [Environment]::GetFolderPath('Startup')
if (-not $startupDirectory) { throw 'The current user has no Windows Startup directory.' }
$entryName = 'Mullvad Domestic Direct Helper.lnk'
$entryPath = Join-Path $startupDirectory $entryName
$powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}"' -f $scriptPath
$plan = [PSCustomObject]@{HelperDirectory=$HelperDirectory;StartupEntry=$entryPath;Target=$powerShellPath;Arguments=$arguments;StartsExistingProcess=$false}
if (-not $PSCmdlet.ShouldProcess($entryPath, ('Enable current-user logon startup for ' + $HelperDirectory))) {
    $plan
    return
}

$approvalPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'
$approval = Get-ItemProperty -LiteralPath $approvalPath -Name $entryName -ErrorAction SilentlyContinue
if ($null -ne $approval -and ([byte[]]$approval.$entryName)[0] -eq 3) {
    throw 'Windows has disabled this startup entry. Re-enable this entry in Startup Apps explicitly; no approval record was changed.'
}
$shellObject = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $entryPath) {
    $existing = $shellObject.CreateShortcut($entryPath)
    $ownershipPrefix = 'Start the domestic direct helper after current-user logon;'
    $description = [string]$existing.Description
    if ($existing.TargetPath -ne $powerShellPath -or $existing.Arguments -notmatch 'Start-Helper\.ps1' -or
        -not $description.StartsWith($ownershipPrefix, [StringComparison]::Ordinal)) {
        throw 'A different startup command uses this name. It was preserved.'
    }
    if ($existing.Arguments -eq $arguments -and $existing.WorkingDirectory.TrimEnd('\') -eq $HelperDirectory) {
        Write-Output 'Autostart already points to the correct directory; nothing changed.'
        $plan
        return
    }
    $backupDirectory = Join-Path $HelperDirectory 'runtime\startup-backup'
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $entryPath -Destination (Join-Path $backupDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '.lnk'))
}
$shortcut = $shellObject.CreateShortcut($entryPath)
$shortcut.TargetPath = $powerShellPath
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = $HelperDirectory
$shortcut.WindowStyle = 7
$shortcut.Description = 'Start the domestic direct helper after current-user logon; preserve an existing instance.'
$shortcut.IconLocation = (Join-Path $HelperDirectory 'mullvad-direct-helper.exe') + ',0'
$shortcut.Save()
$saved = $shellObject.CreateShortcut($entryPath)
if ($saved.TargetPath -ne $powerShellPath -or $saved.Arguments -ne $arguments -or $saved.WorkingDirectory.TrimEnd('\') -ne $HelperDirectory) {
    throw 'Startup shortcut verification failed.'
}
$plan
Write-Output 'Registered for current-user logon. The running helper and network settings were not changed.'
