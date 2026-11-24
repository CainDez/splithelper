[CmdletBinding()]
param([string]$Proxy)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version = '1.19.33'
$asset = "mihomo-windows-amd64-compatible-v$version.zip"
$expected = '7aaf636a4f2172dcd8ec34ac4f52797b059195e5afd6409508c963e545f8864d'
$cache = Join-Path $repo '.tools\downloads'
$component = Join-Path $repo 'desktop\SplitHelper\components\direct-helper'
$mainComponent = Join-Path $repo 'desktop\SplitHelper\components\main-core'
$licenses = Join-Path $repo 'desktop\SplitHelper\licenses'
New-Item -ItemType Directory -Path $cache, $component, $mainComponent, $licenses -Force | Out-Null
function Receive-Artifact([string]$Uri, [string]$Destination) {
    $arguments = @('--fail', '--location', '--retry', '2', '--max-time', '240', '--output', $Destination, $Uri)
    if ($Proxy) { $arguments = @('--proxy', $Proxy) + $arguments }
    & curl.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Uri" }
}
$archive = Join-Path $cache $asset
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    Receive-Artifact "https://github.com/MetaCubeX/mihomo/releases/download/v$version/$asset" $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Mihomo archive checksum mismatch.' }
$extract = Join-Path $cache ('extract-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $executables = @(Get-ChildItem -LiteralPath $extract -Filter '*.exe' -Recurse -File)
    if ($executables.Count -ne 1) { throw 'Expected exactly one Mihomo executable.' }
    $target = Join-Path $component 'mullvad-direct-helper.exe'
    Copy-Item -LiteralPath $executables[0].FullName -Destination $target -Force
    $manifest = [ordered]@{ version = $version; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant(); configSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'direct.yaml') -Algorithm SHA256).Hash.ToLowerInvariant(); archiveSha256 = $expected; upstream = "https://github.com/MetaCubeX/mihomo/tree/v$version" }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $component 'manifest.json') -Encoding utf8
    Copy-Item -LiteralPath $executables[0].FullName -Destination (Join-Path $mainComponent 'split-routing-core.exe') -Force
} finally {
    $resolvedExtract = [IO.Path]::GetFullPath($extract)
    if ($resolvedExtract.StartsWith([IO.Path]::GetFullPath($cache) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedExtract)) { Remove-Item -LiteralPath $resolvedExtract -Recurse -Force }
}
# The upstream has only a moving latest tag. Pin its release branch commit and
# hashes so a future upstream update cannot silently change the shipped rules.
$rulesCommit = '38711db8318645f994b3b0e2ab4332e87bbfb02c'
$rulesVersion = '2026-11-10 12:12'
$rulesSource = "https://github.com/MetaCubeX/meta-rules-dat/tree/$rulesCommit"
$ruleFiles = @(
    @{ upstream = 'geosite.dat'; target = 'geosite.dat'; sha256 = '6dc3c32192511451a8eb706e224b00197d748a2d0a05a7c6b750536098b5daf7' },
    @{ upstream = 'country.mmdb'; target = 'Country.mmdb'; sha256 = 'c0c6ea5661a3167abb24bbbaf629605aa3856c425384cbf2897c5a0f41027663' }
)
foreach ($file in $ruleFiles) {
    $download = Join-Path $cache ($rulesCommit + '-' + $file.upstream)
    if (-not (Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
        Receive-Artifact "https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/$rulesCommit/$($file.upstream)" $download
    }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Rule database checksum mismatch: $($file.upstream)" }
    Copy-Item -LiteralPath $download -Destination (Join-Path $mainComponent $file.target) -Force
}
$mainManifest = [ordered]@{
    version = $version
    sha256 = (Get-FileHash -LiteralPath (Join-Path $mainComponent 'split-routing-core.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    archiveSha256 = $expected
    upstream = "https://github.com/MetaCubeX/mihomo/tree/v$version"
    geositeSha256 = $ruleFiles[0].sha256
    countrySha256 = $ruleFiles[1].sha256
    geodataVersion = $rulesVersion
    geodataCommit = $rulesCommit
    geodataSource = $rulesSource
}
$mainManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mainComponent 'manifest.json') -Encoding utf8
$ruleNotices = Join-Path $licenses 'rule-data'
if (-not (Test-Path -LiteralPath (Join-Path $ruleNotices 'NOTICE.md'))) { throw 'Missing bundled rule-data notices.' }
$noticeSources = Get-Content -LiteralPath (Join-Path $ruleNotices 'sources.json') -Raw | ConvertFrom-Json
foreach ($notice in $noticeSources) {
    if ((Get-FileHash -LiteralPath (Join-Path $ruleNotices $notice.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $notice.sha256) { throw "Rule-data notice checksum mismatch: $($notice.file)" }
}
$rulesBuildCommit = '4178770badecb1b349fbcd62c737e0d7a2079729'
$rulesBuildSource = Join-Path $ruleNotices "meta-rules-dat-source-$rulesBuildCommit.zip"
if (-not (Test-Path -LiteralPath $rulesBuildSource)) { Receive-Artifact "https://github.com/MetaCubeX/meta-rules-dat/archive/$rulesBuildCommit.zip" $rulesBuildSource }
$license = Join-Path $licenses 'Mihomo-GPL-3.0.txt'
Receive-Artifact "https://raw.githubusercontent.com/MetaCubeX/mihomo/v$version/LICENSE" $license
$licenseHeader = (Get-Content -LiteralPath $license -TotalCount 3) -join [Environment]::NewLine
if ($licenseHeader -notmatch 'GNU GENERAL PUBLIC LICENSE') { throw 'Unexpected Mihomo license.' }
$source = Join-Path $licenses "mihomo-source-v$version.zip"
if (-not (Test-Path -LiteralPath $source)) { Receive-Artifact "https://github.com/MetaCubeX/mihomo/archive/refs/tags/v$version.zip" $source }
@"
Mihomo $version (Windows amd64 compatible / GoAMD64 v1)
Upstream release: https://github.com/MetaCubeX/mihomo/releases/tag/v$version
Source: https://github.com/MetaCubeX/mihomo/tree/v$version
License: GNU GPL version 3. See Mihomo-GPL-3.0.txt.
The unmodified upstream source archive is included in this directory.
Archive SHA256: $expected
Binary SHA256: $($manifest.sha256)
The same upstream executable is also bundled as components/main-core/split-routing-core.exe.
The main core uses the pinned MetaCubeX meta-rules-dat release commit $rulesCommit ($rulesVersion).
Rules source: $rulesSource
geosite.dat SHA256: $($ruleFiles[0].sha256)
Country.mmdb SHA256: $($ruleFiles[1].sha256)
Source archive SHA256: $((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant())
"@ | Set-Content -LiteralPath (Join-Path $licenses 'Mihomo-NOTICE.txt') -Encoding utf8
Write-Output "Prepared bundled Mihomo $version, checksum verified."
