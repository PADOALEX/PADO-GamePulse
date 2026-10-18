[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectAssetsPath,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$RuntimeVersion = '10.0.13'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$assets = Get-Content -LiteralPath $ProjectAssetsPath -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
if ($packageRoots.Count -eq 0) { throw 'The restored production assets file has no package folders.' }
if (-not ($assets.targets.PSObject.Properties.Name -contains 'net10.0/win-x64')) {
    throw 'Restore/publish the production project for win-x64 before collecting notices.'
}
$noticesRoot = Join-Path $publishRoot 'third-party-notices'
if (Test-Path -LiteralPath $noticesRoot) { throw 'Use a fresh publish directory: notices already exist.' }
New-Item -ItemType Directory -Path $noticesRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs\licenses') -Destination (Join-Path $noticesRoot 'sources') -Recurse

function Find-Package([string]$RelativePath) {
    foreach ($root in $packageRoots) {
        $candidate = Join-Path $root $RelativePath
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw ('A restored package directory is missing: ' + $RelativePath)
}

$packages = @()
foreach ($entry in ($assets.libraries.PSObject.Properties | Sort-Object Name)) {
    if ($entry.Value.type -ne 'package') { continue }
    $identity = $entry.Name.Split('/')
    $id = $identity[0]
    $version = $identity[1]
    $packagePath = Find-Package $entry.Value.path
    $nuspec = @(Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File)
    if ($nuspec.Count -ne 1) { throw ('Expected one package specification: ' + $entry.Name) }
    [xml]$specification = Get-Content -LiteralPath $nuspec[0].FullName -Raw
    $metadata = $specification.package.metadata
    $license = [string]$metadata.license.InnerText
    $destinationName = $id + '-' + $version
    $destination = Join-Path $noticesRoot $destinationName
    $licenseFiles = @()
    $packageNotices = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object {
        $_.Name -match '(?i)^(licen[cs]e|copying|third[-_. ]?party[-_. ]?notices|notice)(\..*)?$'
    })
    if ($packageNotices.Count -gt 0) {
        New-Item -ItemType Directory -Path $destination | Out-Null
        foreach ($file in $packageNotices) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)
            $licenseFiles += 'third-party-notices/' + $destinationName + '/' + $file.Name
        }
    }
    # Some packages declare MIT but omit its text. Retain the corresponding upstream text,
    # pinned to the exact repository commit recorded by those production packages.
    if ($id -eq 'Avalonia.BuildServices') { $fallback = @('Avalonia.BuildServices-LICENSE.txt') }
    elseif ($id -like 'Avalonia*' -and $id -ne 'Avalonia.Angle.Windows.Natives') { $fallback = @('Avalonia-LICENSE.md', 'Avalonia-NOTICE.md') }
    elseif ($id -eq 'MicroCom.Runtime') { $fallback = @('MicroCom-LICENSE.txt') }
    elseif ($id -like 'Microsoft.Extensions.*') { $fallback = @('Microsoft-LICENSE.txt') }
    elseif ($id -eq 'Tmds.DBus.Protocol') { $fallback = @('Tmds.DBus-COPYING.txt') }
    else { $fallback = @() }
    foreach ($file in $fallback) { $licenseFiles += 'third-party-notices/sources/' + $file }
    if ($licenseFiles.Count -eq 0) { throw ('No license/notice text collected for ' + $entry.Name) }
    $repositoryUrl = $null
    $repositoryCommit = $null
    if ($metadata.repository) {
        $repositoryUrl = [string]$metadata.repository.url
        $repositoryCommit = [string]$metadata.repository.commit
    }
    $packages += [ordered]@{
        id = $id; version = $version; declaredLicense = $license
        repository = $repositoryUrl; repositoryCommit = $repositoryCommit
        licenseFiles = $licenseFiles
    }
}

$runtimePath = Find-Package ('microsoft.netcore.app.runtime.win-x64/' + $RuntimeVersion)
$runtimeNotices = @(Get-ChildItem -LiteralPath $runtimePath -File | Where-Object {
    $_.Name -match '(?i)^(licen[cs]e|third[-_. ]?party[-_. ]?notices)(\..*)?$'
})
if ($runtimeNotices.Count -lt 2) { throw 'The pinned runtime package must include LICENSE and THIRD-PARTY-NOTICES.' }
$runtimeDestinationName = 'Microsoft.NETCore.App.Runtime.win-x64-' + $RuntimeVersion
$runtimeDestination = Join-Path $noticesRoot $runtimeDestinationName
New-Item -ItemType Directory -Path $runtimeDestination | Out-Null
$runtimeLicenseFiles = @()
foreach ($file in $runtimeNotices) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $runtimeDestination $file.Name)
    $runtimeLicenseFiles += 'third-party-notices/' + $runtimeDestinationName + '/' + $file.Name
}
$manifest = [ordered]@{
    scope = 'Resolved production Desktop project dependency graph, including cross-platform and build-time dependencies; test projects are excluded.'
    target = 'net10.0/win-x64'
    runtime = [ordered]@{ id = 'Microsoft.NETCore.App.Runtime.win-x64'; version = $RuntimeVersion; licenseFiles = $runtimeLicenseFiles }
    packages = $packages
}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $publishRoot 'DEPENDENCIES.json'), ($manifest | ConvertTo-Json -Depth 8), $utf8)
$notice = @(
    '# Third-party notices', '',
    'Dependency licenses remain with their respective owners. These notices do not select a license for PADO GamePulse.', '',
    'DEPENDENCIES.json records the restored production dependency graph. It can include build tools and other-platform packages; it contains no test-project dependency graph.', '',
    ('The Windows bundle includes Microsoft.NETCore.App.Runtime.win-x64 ' + $RuntimeVersion + '. Its license and third-party notices are included below third-party-notices/.'), '',
    'Full package/upstream license and notice texts are retained in third-party-notices/. Source provenance is recorded in third-party-notices/sources/README.md.', '',
    'Keep this file, DEPENDENCIES.json and the entire third-party-notices directory with the application when copying it.'
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $publishRoot 'THIRD_PARTY_NOTICES.md'), $notice, $utf8)
Write-Host ('Collected notices for ' + $packages.Count + ' production-graph packages and .NET ' + $RuntimeVersion + '.')
