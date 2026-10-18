[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath,[Parameter(Mandatory=$true)][string]$TestDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
$installer=(Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot=[IO.Path]::GetFullPath($TestDirectory)
Assert-GamePulseOrdinaryPath $installer
Assert-GamePulseOrdinaryPath $testRoot
[xml]$properties=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw
$version=[string]$properties.Project.PropertyGroup.Version
$installRoot=Join-Path $testRoot 'installed-app'
$registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7BE24789-A462-4F37-9203-BA468744067E}_is1'
$shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'PADO GamePulse.lnk'
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $testRoot)) {
    throw 'A prior registration/shortcut/test install exists. Refusing to overwrite it.'
}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$previousKey=[Environment]::GetEnvironmentVariable('GAMEPULSE_STEAM_API_KEY','Process')
function Run-Checked([string]$File,[string[]]$Arguments) {
    $process=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Installer check child failed: '+[IO.Path]::GetFileName($File)+' exit '+$process.ExitCode) }
}
$previousDotEnv=[Environment]::GetEnvironmentVariable('GAMEPULSE_DISABLE_DOTENV','Process')

try {
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' '1'
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $null
    Run-Checked $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$installRoot+'"'),('/LOG="'+(Join-Path $testRoot 'install.log')+'"'))
    $exe=Join-Path $installRoot 'GamePulse.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $shortcut)) { throw 'Installed executable or Start menu shortcut missing.' }
    $registered=Get-ItemProperty -LiteralPath $registration
    if ($registered.InstallLocation.TrimEnd('\') -ne $installRoot.TrimEnd('\') -or $registered.DisplayVersion -ne $version) { throw 'Unexpected per-user uninstall registration.' }
    $shell=New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'Shortcut does not target the installed application.' }
    Run-Checked $exe @('--smoke-test','--smoke-output',('"'+(Join-Path $testRoot 'installed-startup.png')+'"'))
    & powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-WindowsAccessibility.ps1') -ExecutablePath $exe -OutputPath (Join-Path $testRoot 'installed-accessibility.json')
    if ($LASTEXITCODE -ne 0) { throw 'Installed application accessibility check failed.' }
    $marker=Join-Path $installRoot 'unmanaged-user-file.txt'
    [IO.File]::WriteAllText($marker,'This unmanaged test file must survive reinstall and uninstall.')
    Run-Checked $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$installRoot+'"'),('/LOG="'+(Join-Path $testRoot 'reinstall.log')+'"'))
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Reinstall removed an unmanaged file.' }
    Run-Checked (Join-Path $installRoot 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'uninstall.log')+'"'))
    if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstall left a registered managed application or shortcut.' }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Uninstall removed an unmanaged file.' }
    $result=[ordered]@{ Passed=$true; Version=$version; PerUserRegistration=$true; StartMenuShortcut=$true; InstalledStartup=$true; NativeAccessibility=$true; SameVersionReinstall=$true; Uninstall=$true; UnmanagedFilePreserved=$true; CleanMachine=$false; Signed=$false }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'installer-results.json') -Encoding UTF8
    Write-Host 'Installer installation, startup, UIA, reinstall and uninstall checks passed.'
}
finally {
    # Only the exact installation created by this test may be cleaned up after a failure.
    if ((Test-Path -LiteralPath $registration) -and (Test-Path -LiteralPath (Join-Path $installRoot 'unins000.exe'))) {
        $location=(Get-ItemProperty -LiteralPath $registration).InstallLocation.TrimEnd('\')
        if ($location -eq $installRoot.TrimEnd('\')) {
            $cleanup=Start-Process -FilePath (Join-Path $installRoot 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
            if ($cleanup.ExitCode -ne 0) { Write-Warning 'The isolated test install could not be uninstalled automatically.' }
        }
    }
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' $previousDotEnv
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $previousKey
}
