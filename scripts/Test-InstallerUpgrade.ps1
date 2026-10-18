[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PreviousInstallerPath,
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$TestDirectory,
    [string]$CompilerPath,
    [switch]$SkipUiAutomation
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
$previousInstaller=(Resolve-Path -LiteralPath $PreviousInstallerPath).Path
$installer=(Resolve-Path -LiteralPath $InstallerPath).Path
$publishRoot=(Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$testRoot=[IO.Path]::GetFullPath($TestDirectory).TrimEnd('\')
Assert-GamePulseOrdinaryPath $previousInstaller
Assert-GamePulseOrdinaryPath $installer
Assert-GamePulseOrdinaryPath $testRoot
Assert-GamePulsePublishPayload $publishRoot
$installRoot=Join-Path $testRoot 'installed-app'
$registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7BE24789-A462-4F37-9203-BA468744067E}_is1'
$startShortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'PADO GamePulse.lnk'
$desktopShortcut=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'PADO GamePulse.lnk'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties=Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw
$version=[string]$properties.Project.PropertyGroup.Version
$previousVersion=ConvertTo-GamePulseReleaseVersion (Get-Item -LiteralPath $previousInstaller).VersionInfo.ProductVersion
if ([Version]$previousVersion -ge [Version]$version) { throw 'An actual older installer is required for the cross-version check.' }
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $startShortcut) -or (Test-Path -LiteralPath $desktopShortcut) -or (Test-Path -LiteralPath $testRoot)) {
    throw 'A product registration, shortcut or test directory already exists. Refusing to overwrite it.'
}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$priorKey=[Environment]::GetEnvironmentVariable('GAMEPULSE_STEAM_API_KEY','Process')
$shell=New-Object -ComObject WScript.Shell
$result=[ordered]@{ Passed=$false; PreviousVersion=$previousVersion; Version=$version; CleanMachine=$false; Signed=$false }
function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Child([string]$File,[string[]]$Arguments,[switch]$ExpectFailure) {
    $process=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($ExpectFailure) { Require ($process.ExitCode -ne 0) 'An installer that must reject this installation returned success.' }
    else { Require ($process.ExitCode -eq 0) ('Child process failed: '+[IO.Path]::GetFileName($File)+' exit '+$process.ExitCode) }
    return $process.ExitCode
}
function Install([string]$File,[string]$LogName,[bool]$SetDirectory) {
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot $LogName)+'"'))
    if ($SetDirectory) { $arguments+=('/DIR="'+$installRoot+'"'); $arguments+='/TASKS=desktopicon' }
    [void](Run-Child $File $arguments)
}
function Require-Registration([string]$ExpectedVersion) {
    Require (Test-Path -LiteralPath $registration) 'Per-user application registration is missing.'
    $registered=Get-ItemProperty -LiteralPath $registration
    Require ($registered.DisplayVersion -eq $ExpectedVersion) 'The registered version is unexpected.'
    Require ($registered.InstallLocation.TrimEnd('\') -eq $installRoot) 'The installer did not preserve the previous installation directory.'
    $exe=Join-Path $installRoot 'GamePulse.Desktop.exe'
    foreach ($shortcut in @($startShortcut,$desktopShortcut)) {
        Require (Test-Path -LiteralPath $shortcut) 'A selected application shortcut is missing.'
        $link=$shell.CreateShortcut($shortcut)
        Require ($link.TargetPath -eq $exe -and $link.WorkingDirectory.TrimEnd('\') -eq $installRoot) 'An installed shortcut has an unexpected target or working directory.'
    }
}
function Require-Payload {
    foreach ($file in @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)) {
        $relative=$file.FullName.Substring($publishRoot.Length+1)
        $installed=Join-Path $installRoot $relative
        Require (Test-Path -LiteralPath $installed -PathType Leaf) ('Missing installed payload file: '+$relative)
        Require ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) ('An installed payload hash differs: '+$relative)
    }
}
function Require-UnmanagedFiles([string]$Stage) {
    Require (Test-Path -LiteralPath $marker -PathType Leaf) ($Stage+' removed the unmanaged user marker.')
    Require ([IO.File]::ReadAllText($marker) -eq $markerText) ($Stage+' changed the unmanaged user marker.')
    # This file belongs only to this isolated test and contains a deliberate non-secret placeholder.
    # No existing user .env is opened, copied or included in the installer payload.
    Require (Test-Path -LiteralPath $environmentFile -PathType Leaf) ($Stage+' removed the unmanaged local .env fixture.')
    Require ([IO.File]::ReadAllText($environmentFile) -eq $environmentFileText) ($Stage+' changed the unmanaged local .env fixture.')
}
function Require-OnlyUnmanagedFiles {
    $cleanupDeadline=[DateTime]::UtcNow.AddSeconds(5)
    do {
        $remaining=@(Get-ChildItem -LiteralPath $installRoot -Recurse -File)
        if ($remaining.Count -eq 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $cleanupDeadline)
    $expected=@($marker,$environmentFile) | Sort-Object
    $actual=@($remaining | ForEach-Object { $_.FullName }) | Sort-Object
    Require ($remaining.Count -eq 2 -and (@(Compare-Object $expected $actual).Count -eq 0)) 'Uninstall left unexpected managed files or removed unmanaged test files.'
}
function Uninstall-Owned {
    if (-not (Test-Path -LiteralPath $registration)) { return }
    $registered=Get-ItemProperty -LiteralPath $registration
    Require ($registered.InstallLocation.TrimEnd('\') -eq $installRoot) 'Refusing to clean a registration outside this test installation.'
    $uninstaller=Join-Path $installRoot 'unins000.exe'
    Require (Test-Path -LiteralPath $uninstaller) 'Owned test uninstaller is missing.'
    [void](Run-Child $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot ('uninstall-'+[Guid]::NewGuid().ToString('N')+'.log'))+'"')))
}
$previousDotEnv=[Environment]::GetEnvironmentVariable('GAMEPULSE_DISABLE_DOTENV','Process')

try {
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' '1'
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $null
    Install $previousInstaller 'install-previous.log' $true
    Require-Registration $previousVersion
    [void](Run-Child (Join-Path $installRoot 'GamePulse.Desktop.exe') @('--smoke-test','--smoke-output',('"'+(Join-Path $testRoot 'previous-startup.png')+'"')))
    Require (Test-Path -LiteralPath (Join-Path $testRoot 'previous-startup.png')) 'Previous-version native startup did not produce its screenshot.'
    $oldFiles=@(Get-ChildItem -LiteralPath $installRoot -Recurse -File | Where-Object { $_.Name -notmatch '^unins\d+\.' } | ForEach-Object { $_.FullName.Substring($installRoot.Length+1) })
    $marker=Join-Path $installRoot 'unmanaged-user-file.txt'
    $markerText='This unmanaged file must survive upgrade, reinstall, rejection and uninstall.'
    [IO.File]::WriteAllText($marker,$markerText)
    $environmentFile=Join-Path $installRoot '.env'
    $environmentFileText="# Synthetic installer fixture only; not a real Steam credential.`nGAMEPULSE_STEAM_API_KEY=test-only-installer-placeholder`n"
    Require (-not (Test-Path -LiteralPath $environmentFile)) 'Previous release unexpectedly installed a local .env file.'
    [IO.File]::WriteAllText($environmentFile,$environmentFileText,(New-Object Text.UTF8Encoding($false)))

    # Omit /DIR and /TASKS: the actual older installer registration must supply both.
    Install $installer 'upgrade.log' $false
    Require-Registration $version
    Require-Payload
    $retired=@($oldFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishRoot $_)) })
    foreach ($relative in $retired) { Require (-not (Test-Path -LiteralPath (Join-Path $installRoot $relative))) ('An unchanged retired managed file survived upgrade: '+$relative) }
    Require-UnmanagedFiles 'Upgrade'
    [void](Run-Child (Join-Path $installRoot 'GamePulse.Desktop.exe') @('--smoke-test','--smoke-output',('"'+(Join-Path $testRoot 'upgraded-startup.png')+'"')))
    Require (Test-Path -LiteralPath (Join-Path $testRoot 'upgraded-startup.png')) 'Upgraded native startup did not produce its screenshot.'
    if (-not $SkipUiAutomation) {
        & powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-WindowsAccessibility.ps1') -ExecutablePath (Join-Path $installRoot 'GamePulse.Desktop.exe') -OutputPath (Join-Path $testRoot 'upgraded-accessibility.json')
        Require ($LASTEXITCODE -eq 0) 'The upgraded application native accessibility check failed.'
    }
    $result.CrossVersionUpgrade=$true
    $result.PreviousDirectoryAndDesktopTaskPreserved=$true
    $result.ManagedPayloadHashes=$true
    $result.ActualRetiredManagedFileCount=$retired.Count
    $result.UpgradedNativeStartup=$true
    $result.UpgradedNativeAccessibility=(-not [bool]$SkipUiAutomation)
    $result.LocalDotEnvPreservedOnUpgrade=$true

    Install $installer 'same-version-reinstall.log' $false
    Require-Registration $version
    Require-Payload
    Require-UnmanagedFiles 'Reinstall'
    $result.SameVersionReinstall=$true
    $result.LocalDotEnvPreservedOnReinstall=$true

    # Old 0.7 setup cannot acquire a retroactive guard. Exercise the current setup's
    # real rejection using an explicitly synthetic future value in our own key only.
    $futureVersion=([Version]::new(([Version]$version).Major,([Math]::Max(10,(([Version]$version).Minor+1))),0)).ToString(3)
    Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $futureVersion
    try {
        $result.FutureRegisteredVersion=$futureVersion
        $result.DowngradeRejectionExitCode=Run-Child $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'reject-future.log')+'"')) -ExpectFailure
        Require ([IO.File]::ReadAllText((Join-Path $testRoot 'reject-future.log')).Contains('A newer PADO GamePulse version ('+$futureVersion+') is already installed.')) 'The expected numeric downgrade guard did not report the rejection.'
        Require-Registration $futureVersion
        Require-Payload
        Require-UnmanagedFiles 'Newer-version rejection'
    }
    finally { Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $version }
    Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value 'unrecognized-test-version'
    try {
        $result.AmbiguousRegisteredVersionExitCode=Run-Child $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'reject-ambiguous-version.log')+'"')) -ExpectFailure
        Require ([IO.File]::ReadAllText((Join-Path $testRoot 'reject-ambiguous-version.log')).Contains('The installed PADO GamePulse version is not recognized.')) 'The expected ambiguous-version guard did not report the rejection.'
        Require-Payload
        Require-UnmanagedFiles 'Ambiguous-version rejection'
    }
    finally { Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $version }
    Remove-ItemProperty -LiteralPath $registration -Name DisplayVersion
    try {
        $result.MissingRegisteredVersionExitCode=Run-Child $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'reject-missing-version.log')+'"')) -ExpectFailure
        Require ([IO.File]::ReadAllText((Join-Path $testRoot 'reject-missing-version.log')).Contains('The installed PADO GamePulse version could not be read.')) 'The expected missing-version guard did not report the rejection.'
        Require-Payload
        Require-UnmanagedFiles 'Missing-version rejection'
    }
    finally { Set-ItemProperty -LiteralPath $registration -Name DisplayVersion -Value $version }
    $result.CurrentInstallerRejectsNewerAndAmbiguous=$true
    $result.LegacyInstallerDowngradeProtected=$false
    $result.LocalDotEnvPreservedOnAllRejections=$true

    Uninstall-Owned
    foreach ($path in @($registration,$startShortcut,$desktopShortcut,(Join-Path $installRoot 'GamePulse.Desktop.exe'))) { Require (-not (Test-Path -LiteralPath $path)) 'Uninstall left managed application registration, executable or shortcuts.' }
    Require-UnmanagedFiles 'Uninstall'
    Require-OnlyUnmanagedFiles
    $result.UninstallAndBothShortcuts=$true
    $result.UnmanagedFilePreserved=$true
    $result.LocalDotEnvPreservedOnUninstall=$true
    $result.RemainingUnmanagedFileCount=2

    if ($CompilerPath) {
        $fixtureRoot=Join-Path $testRoot 'TEST-ONLY-retirement-fixture'
        $fixturePublish=Join-Path $fixtureRoot 'publish'
        $fixtureOutput=Join-Path $fixtureRoot 'installer'
        New-Item -ItemType Directory -Path $fixturePublish | Out-Null
        foreach ($entry in @(Get-ChildItem -LiteralPath $publishRoot -Force)) { Copy-Item -LiteralPath $entry.FullName -Destination $fixturePublish -Recurse }
        $removed='GamePulse.Core.pdb'
        $changed='GamePulse.Infrastructure.pdb'
        foreach ($relative in @($removed,$changed)) {
            $target=[IO.Path]::GetFullPath((Join-Path $fixturePublish $relative))
            Require ($target.StartsWith(($fixturePublish+'\'),[StringComparison]::OrdinalIgnoreCase)) 'Fixture file escapes its workspace folder.'
            Remove-Item -LiteralPath $target
        }
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -PublishDirectory $fixturePublish -CompilerPath $CompilerPath -OutputDirectory $fixtureOutput
        $fixtureInstaller=Join-Path $fixtureOutput ('PADO.GamePulse-'+$version+'-win-x64-setup.exe')
        Install $previousInstaller 'retirement-install-previous.log' $true
        Require-UnmanagedFiles 'Fixture previous-version install'
        $changedPath=Join-Path $installRoot $changed
        [IO.File]::WriteAllText($changedPath,'A deliberate local edit of a formerly managed file must be preserved during upgrade.')
        Install $fixtureInstaller 'retirement-upgrade-TEST-ONLY.log' $false
        Require-Registration $version
        Require (-not (Test-Path -LiteralPath (Join-Path $installRoot $removed))) 'An unchanged hash-matched retired file was not removed.'
        Require ([IO.File]::ReadAllText($changedPath) -eq 'A deliberate local edit of a formerly managed file must be preserved during upgrade.') 'A modified retired file was removed.'
        Require-UnmanagedFiles 'Retirement cleanup'
        [void](Run-Child (Join-Path $installRoot 'GamePulse.Desktop.exe') @('--smoke-test'))
        $result.TestOnlyRetirementFixture=$true
        $result.HashMatchedRetiredFileRemoved=$true
        $result.ModifiedRetiredFilePreservedDuringUpgrade=$true
        $result.LocalDotEnvPreservedOnRetirementCleanup=$true
        Uninstall-Owned
        foreach ($path in @($registration,$startShortcut,$desktopShortcut,(Join-Path $installRoot 'GamePulse.Desktop.exe'))) { Require (-not (Test-Path -LiteralPath $path)) 'Fixture uninstall left managed registration, executable or shortcuts.' }
        Require-UnmanagedFiles 'Fixture uninstall'
        Require-OnlyUnmanagedFiles
        $result.LocalDotEnvPreservedOnFixtureUninstall=$true
    }
    else { $result.TestOnlyRetirementFixture=$false }
    $result.Passed=$true
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'installer-upgrade-results.json') -Encoding UTF8
    Write-Host 'Cross-version installation, startup, payload, rejection, shortcut and uninstall checks passed.'
}
finally {
    try { Uninstall-Owned } catch { Write-Warning 'The exact owned test installation could not be uninstalled automatically; inspect the isolated test logs.' }
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' $previousDotEnv
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $priorKey
}
