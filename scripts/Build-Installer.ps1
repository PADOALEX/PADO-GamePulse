[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot=(Resolve-Path -LiteralPath $PublishDirectory).Path
$compiler=(Resolve-Path -LiteralPath $CompilerPath).Path
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
Assert-GamePulseOrdinaryPath $sourceRoot
Assert-GamePulseOrdinaryPath $compiler
Assert-GamePulseOrdinaryPath $outputRoot
Assert-GamePulsePublishPayload $publishRoot
if ($outputRoot.TrimEnd('\','/').Equals($publishRoot.TrimEnd('\','/'),[StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith(($publishRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep installer output outside the input publish directory.'
}
[xml]$properties=Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw
$version=[string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A numeric three-part version is required.' }
$parsedVersion=[Version]$version
if ($parsedVersion.Major -gt 65535 -or $parsedVersion.Minor -gt 65535 -or $parsedVersion.Build -gt 65535) { throw 'Each installer version component must fit the documented 16-bit packed-version format.' }
$compilerHash=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
if ($compilerHash -ne 'D06EBD38F38E3CEE60A3C50CC45BD449D77E0BC6A5CABC607EA9886808E4DE1A') { throw 'Use the verified Inno Setup 7.1.0 x64 compiler.' }
$signature=Get-AuthenticodeSignature -FilePath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Compiler publisher verification failed.' }
foreach ($file in @('GamePulse.Desktop.exe','coreclr.dll','DEPENDENCIES.json','THIRD_PARTY_NOTICES.md','START_HERE.txt','LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $file))) { throw ('Missing verified publish file: '+$file) }
}
if ((ConvertTo-GamePulseReleaseVersion (Get-Item -LiteralPath (Join-Path $publishRoot 'GamePulse.Desktop.exe')).VersionInfo.ProductVersion) -ne $version) { throw 'Published app version mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $publishRoot 'LICENSE') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Algorithm SHA256).Hash) { throw 'Published project license does not match the source license.' }
$runtimeConfiguration=Get-Content -LiteralPath (Join-Path $publishRoot 'GamePulse.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
$included=@($runtimeConfiguration.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
if ($included.Count -ne 1 -or $included[0].version -ne '10.0.13') { throw 'Use the verified self-contained .NET 10.0.13 publish output.' }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$target=Join-Path $outputRoot ('PADO.GamePulse-'+$version+'-win-x64-setup.exe')
$retirementInclude=Join-Path $outputRoot ('retired-managed-'+$version+'.iss')
foreach ($existing in @($target,($target+'.sha256'),$retirementInclude,(Join-Path $outputRoot ('retirement-build-'+$version+'.json')))) {
    if (Test-Path -LiteralPath $existing) { throw 'Installer output already exists; use a fresh output directory.' }
}
$retirement=& (Join-Path $PSScriptRoot 'New-InstallerRetirementInclude.ps1') -PublishDirectory $publishRoot -OutputPath $retirementInclude
$retirement | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot ('retirement-build-'+$version+'.json')) -Encoding UTF8
& $compiler --quiet-progress --no-ide-signtools ('--define=AppVersion='+$version) ('--define=PublishDir='+$publishRoot) ('--define=InstallerOutput='+$outputRoot) ('--define=RetirementInclude='+$retirementInclude) (Join-Path $sourceRoot 'installer\GamePulse.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target)) { throw 'Installer compilation failed.' }
$hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($target+'.sha256'),($hash+'  '+[IO.Path]::GetFileName($target)+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Installer: '+$target)
