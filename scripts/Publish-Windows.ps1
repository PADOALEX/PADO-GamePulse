[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipVerification,
    [switch]$SkipNativeAccessibility
)

# Run with Windows PowerShell 5.1 or PowerShell 7 on Windows. No global installs, secrets,
# existing release deletion or external publication are performed by this script.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required for the native startup smoke checks in this release script.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sourceRoot 'artifacts\windows' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
Assert-GamePulseOrdinaryPath $sourceRoot
Assert-GamePulseOrdinaryPath $outputRoot
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
[xml]$properties = Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$') { throw 'A safe release Version is required in Directory.Build.props.' }
$runtimeVersion = '10.0.13'
$releaseName = 'PADO.GamePulse-' + $version + '-win-x64'
$archivePath = Join-Path $outputRoot ($releaseName + '.zip')
$checksumPath = Join-Path $outputRoot ($releaseName + '.sha256')
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) {
    throw 'This release archive/checksum already exists. Select a fresh output directory to preserve the previous release.'
}
$runDirectory = Join-Path $outputRoot ('build-' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $runDirectory 'PADO.GamePulse-win-x64'
$checkDirectory = Join-Path $runDirectory 'checks'
New-Item -ItemType Directory -Path $publishDirectory, $checkDirectory | Out-Null
$desktopProject = Join-Path $sourceRoot 'src\GamePulse.Desktop\GamePulse.Desktop.csproj'
$solution = Join-Path $sourceRoot 'PADO.GamePulse.sln'

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet ' + $Arguments[0] + ' failed with exit code ' + $LASTEXITCODE + '.') }
}

$previousKey = [Environment]::GetEnvironmentVariable('GAMEPULSE_STEAM_API_KEY', 'Process')
$previousLanguage = [Environment]::GetEnvironmentVariable('GAMEPULSE_UI_LANGUAGE', 'Process')
Push-Location -LiteralPath $sourceRoot
$previousDotEnv=[Environment]::GetEnvironmentVariable('GAMEPULSE_DISABLE_DOTENV','Process')

try {
    # This release pipeline uses fixtures only in test projects and exercises the real
    # Desktop setup screen. Credentialed live-source checks are a separate opt-in task.
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' '1'
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $null
    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' 'en'
    if (-not $SkipVerification) {
        Invoke-Dotnet -Arguments @('restore', $solution, '--disable-parallel', '-m:1')
        Invoke-Dotnet -Arguments @('build', $solution, '-c', 'Release', '--no-restore', '-m:1')
        Invoke-Dotnet -Arguments @('test', $solution, '-c', 'Release', '--no-build', '--no-restore', '-m:1', '--logger', 'trx', '--results-directory', (Join-Path $checkDirectory 'test-results'))
        Invoke-Dotnet -Arguments @('run', '--project', (Join-Path $sourceRoot 'tests\GamePulse.UiChecks'), '-c', 'Release', '--no-build', '--', (Join-Path $checkDirectory 'ui-checks'))
    }
    else { Write-Warning 'Build, unit/integration tests and UI checks were skipped. Do not claim they passed based on this invocation.' }

    Invoke-Dotnet -Arguments @('publish', $desktopProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-m:1',
        ('-p:RuntimeFrameworkVersion=' + $runtimeVersion), '-p:TargetLatestRuntimePatch=true', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-o', $publishDirectory)
    Assert-GamePulsePublishPayload $publishDirectory
    foreach ($required in @('GamePulse.Desktop.exe', 'GamePulse.Desktop.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required))) { throw ('Self-contained output is missing ' + $required) }
    }
    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $publishDirectory 'GamePulse.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    $included = @($runtimeConfiguration.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
    if ($included.Count -ne 1 -or $included[0].version -ne $runtimeVersion) { throw 'The produced self-contained runtime version does not match the pinned patch.' }
    & (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -ProjectAssetsPath (Join-Path $sourceRoot 'src\GamePulse.Desktop\obj\project.assets.json') -PublishDirectory $publishDirectory -RuntimeVersion $runtimeVersion
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE')

    $utf8 = New-Object Text.UTF8Encoding($false)
    $instructions = @(
        ('PADO GamePulse ' + $version + ' - Windows x64 portable'), '',
        'Extract the WHOLE folder and launch GamePulse.Desktop.exe. The .NET 10.0.13 runtime is included; a separate .NET installation is not required.', '',
        'This is an unsigned portable application, not an installer. No Steam login, account, server or database is required.',
        'Launch without a key to read setup guidance. Protected Steam Web API methods require your own permitted Steam Web API key.',
        'Never paste a key into support reports. Do not reuse a key distributed inside the old Python archive; revoke/rotate it.',
        'Use SteamID64, an exact supported vanity slug or a Steam Community profile URL. Profile display names are not globally unique.',
        'Steam data can be unavailable, private, incomplete or rate limited. Missing facts do not imply zero or clean status.', '',
        'Keep DEPENDENCIES.json, THIRD_PARTY_NOTICES.md and third-party-notices/ with this folder.',
        'The included runtime does not receive patches from a machine-wide .NET update. Obtain a rebuilt GamePulse release when its runtime requires security updates.',
        'PADO GamePulse is licensed under MIT, selected by the owner. Keep LICENSE and all separate dependency notices.', '',
        'Keyboard: Ctrl+L focuses profile lookup; Ctrl+F focuses the active-page filter; Enter searches; Esc closes active popups before canceling. Ctrl+C in Games/Friends copies the selected ID.', '',
        'Use the language button in the sidebar to switch English/Ukrainian immediately. Profiles, filters and selections remain open. The button changes this session; number/date formatting follows Windows.', '',
        'One-time local setup: create a text file named .env (not .env.txt) beside GamePulse.Desktop.exe, with these two lines:',
        'GAMEPULSE_STEAM_API_KEY=your_own_key_here',
        'GAMEPULSE_UI_LANGUAGE=uk',
        'Save the file and launch GamePulse.Desktop.exe. No terminal key command is needed. Restart after editing the file.',
        'The local .env is plaintext: keep it private. Never send it, add it to Git, or include it when sharing your customized application folder. Release ZIP/setup contain no local key file.',
        'Configuration priority per setting: existing process environment variable, working-directory .env, then application-directory .env. No parent directories are searched. GAMEPULSE_DISABLE_DOTENV=1 disables file loading.', '',
        'Friends and inventory rows load previews as they become visible. Library Pulse summarizes available library playtime with explicit coverage limits; it does not invent historical data.'
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'START_HERE.txt'), $instructions, $utf8)

    $executable = Join-Path $publishDirectory 'GamePulse.Desktop.exe'
    foreach ($scenario in @('dark', 'light', 'contrast', 'minimum-dark', 'minimum-light', 'minimum-contrast')) {
        $imagePath = Join-Path $checkDirectory ('native-setup-' + $scenario + '.png')
        $arguments = @('--smoke-test', '--smoke-output', ('"' + $imagePath + '"'))
        if ($scenario -like '*light') { $arguments += '--smoke-light' }
        if ($scenario -like '*contrast') { $arguments += '--smoke-contrast' }
        if ($scenario -like 'minimum-*') { $arguments += '--smoke-minimum' }
        $process = Start-Process -FilePath $executable -WorkingDirectory $publishDirectory -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) {
            throw ('The native ' + $scenario + ' setup smoke check failed. Exit code: ' + $process.ExitCode)
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'uk\GamePulse.Desktop.resources.dll'))) {
        throw 'The Ukrainian resource satellite is missing from the self-contained output.'
    }
    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' 'uk'
    foreach ($scenario in @('uk-dark', 'uk-minimum-dark')) {
        $imagePath = Join-Path $checkDirectory ('native-setup-' + $scenario + '.png')
        $arguments = @('--smoke-test', '--smoke-output', ('"' + $imagePath + '"'))
        if ($scenario -eq 'uk-minimum-dark') { $arguments += '--smoke-minimum' }
        $process = Start-Process -FilePath $executable -WorkingDirectory $publishDirectory -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) { throw ('Native localized startup failed: ' + $scenario) }
    }
    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' 'en'
    # A file cannot be used as a screenshot's parent directory. Verify that a real
    # smoke failure reaches the native process exit code rather than returning success.
    $blockedParent = Join-Path $checkDirectory 'blocked-output-parent'
    [IO.File]::WriteAllText($blockedParent, 'Intentional startup smoke failure test.', $utf8)
    $invalidOutput = Join-Path $blockedParent 'image.png'
    $failureProcess = Start-Process -FilePath $executable -WorkingDirectory $publishDirectory -ArgumentList @('--smoke-test', '--smoke-output', ('"' + $invalidOutput + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($failureProcess.ExitCode -ne 1) { throw ('Smoke failures must exit 1; observed ' + $failureProcess.ExitCode) }
    if (-not $SkipNativeAccessibility) {
        & powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Check-WindowsAccessibility.ps1') -ExecutablePath $executable -OutputPath (Join-Path $checkDirectory 'native-accessibility-results.json')
        if ($LASTEXITCODE -ne 0) { throw 'Native Windows UI Automation verification failed.' }
    }
    else {
        Write-Warning 'Native UI Automation was explicitly skipped. Headless peer checks do not establish spoken Narrator output.'
        '{"Passed":null,"Skipped":true,"Reason":"Explicit SkipNativeAccessibility","NarratorSpokenReview":false}' | Set-Content -LiteralPath (Join-Path $checkDirectory 'native-accessibility-results.json') -Encoding UTF8
    }

    Assert-GamePulsePublishPayload $publishDirectory
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, ($hash + '  ' + [IO.Path]::GetFileName($archivePath) + [Environment]::NewLine), $utf8)
    Write-Host ('Windows archive: ' + $archivePath)
    Write-Host ('SHA256 file: ' + $checksumPath)
    Write-Host ('Checks/staging retained: ' + $runDirectory)
}
finally {
    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' $previousDotEnv
    Set-GamePulseProcessSetting 'GAMEPULSE_STEAM_API_KEY' $previousKey
    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' $previousLanguage
    Pop-Location
}
