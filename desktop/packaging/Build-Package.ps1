[CmdletBinding()]
param([string]$Dotnet, [string]$Proxy)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Dotnet) { $localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'; $Dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet.exe).Source } }
[xml]$properties = Get-Content -LiteralPath (Join-Path $repo 'desktop\Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
& (Join-Path $PSScriptRoot 'Prepare-Helper.ps1') -Proxy $Proxy
if (-not (Test-Path -LiteralPath (Join-Path $repo 'desktop\SplitHelper\Assets\app.ico'))) { throw 'Missing application icon; run AssetBuilder first.' }
$output = Join-Path $repo ('out\desktop-v' + $version)
$publish = Join-Path $output 'SplitHelper-win-x64'
if (Test-Path -LiteralPath $publish) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publish).Path
    $allowedOutput = [IO.Path]::GetFullPath((Join-Path $repo 'out')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPublish.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedPublish) -ne 'SplitHelper-win-x64') { throw 'Refusing to clean a publish directory outside this project output.' }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
New-Item -ItemType Directory -Path $output, $publish -Force | Out-Null
& $Dotnet publish (Join-Path $repo 'desktop\SplitHelper\SplitHelper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
foreach ($required in @('SplitHelper.exe','components\direct-helper\mullvad-direct-helper.exe','components\direct-helper\direct.yaml','components\direct-helper\manifest.json','components\main-core\split-routing-core.exe','components\main-core\manifest.json','components\main-core\geosite.dat','components\main-core\Country.mmdb','licenses\Mihomo-GPL-3.0.txt')) { if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Missing publish output: $required" } }
$commit = (& git -C $repo rev-parse HEAD).Trim()
$dirty = [bool](& git -C $repo status --porcelain --untracked-files=normal)
[ordered]@{version=$version;commit=$commit;workingTreeModified=$dirty;rid='win-x64';selfContained=$true;builtAt=(Get-Date).ToUniversalTime().ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'build-info.json') -Encoding utf8
$installer = Join-Path $output "SplitHelper-Setup-$version-win-x64.exe"
$nsis = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'
if (-not (Test-Path -LiteralPath $nsis)) { throw 'NSIS compiler not found.' }
& $nsis /INPUTCHARSET UTF8 /WX "/DVERSION=$version" "/DAPPDIR=$publish" "/DOUTPUT=$installer" ('/DICONFILE=' + (Join-Path $repo 'desktop\SplitHelper\Assets\app.ico')) (Join-Path $PSScriptRoot 'installer.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$portable = Join-Path $output "SplitHelper-$version-win-x64.zip"
Compress-Archive -LiteralPath $publish -DestinationPath $portable -Force
$sums = foreach ($file in @($installer, $portable)) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file) }
$sums | Set-Content -LiteralPath (Join-Path $output 'checksums.sha256') -Encoding ascii
Write-Output "Built SplitHelper $version at $output"
