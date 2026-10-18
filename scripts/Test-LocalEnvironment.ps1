[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [Parameter(Mandatory=$true)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')

function Assert-OrdinaryAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Local configuration checks require ordinary paths, without junctions or symbolic links.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }


$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$executableDirectory = [IO.Path]::GetDirectoryName($executable)
$testRoot = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\','/')
Assert-OrdinaryAncestors $executable
Assert-OrdinaryAncestors $testRoot
if (Test-Path -LiteralPath $testRoot) { throw 'Choose a fresh isolated test directory; existing files are never changed.' }
if ($testRoot.Equals($executableDirectory,[StringComparison]::OrdinalIgnoreCase) -or
    $testRoot.StartsWith(($executableDirectory + [IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep the isolated configuration checks outside the published application directory.'
}
if (Test-Path -LiteralPath (Join-Path $executableDirectory '.env')) {
    # Inspect existence only. Never open an existing user's adjacent configuration.
    throw 'Select a clean published application without an adjacent .env. Existing local configuration is never read by this test.'
}
$workingDirectory = Join-Path $testRoot 'isolated-working-directory'
$reportsDirectory = Join-Path $testRoot 'startup-reports'
New-Item -ItemType Directory -Path $workingDirectory,$reportsDirectory | Out-Null
Assert-OrdinaryAncestors $testRoot
$ownedEnvironment = Join-Path $workingDirectory '.env'
$utf8 = New-Object Text.UTF8Encoding($false)
$placeholder = 'TEST_ONLY_LOCAL_CONFIGURATION_PROBE_NOT_A_STEAM_KEY'
$prior = @{}
foreach ($name in @('GAMEPULSE_STEAM_API_KEY','GAMEPULSE_UI_LANGUAGE','GAMEPULSE_DISABLE_DOTENV')) {
    $prior[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
}

function Invoke-StartupCheck([string]$Scenario,[bool]$Configured,[bool]$Setup,[string]$Language) {
    $reportPath = Join-Path $reportsDirectory ($Scenario + '.json')
    $process = $null
    try {
        # Only application startup is requested. There is no profile argument, search action,
        # real API key, live lookup or screenshot/desktop inspection in this check.
        $process = Start-Process -FilePath $executable -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru `
            -ArgumentList @('--smoke-test','--smoke-report',('"' + $reportPath + '"'))
        # Retain the native process handle so Windows PowerShell can read the exit code
        # even when a short-lived smoke process exits before its next property access.
        $null = $process.Handle
        if (-not $process.WaitForExit(20000)) { throw 'The isolated configuration startup check timed out.' }
        Require ($process.ExitCode -eq 0) 'The isolated configuration startup process failed.'
        Require (Test-Path -LiteralPath $reportPath -PathType Leaf) 'The startup configuration report was not created.'
        $raw = [IO.File]::ReadAllText($reportPath)
        Require (-not $raw.Contains($placeholder)) 'A test-only key value reached the startup report.'
        $report = $raw | ConvertFrom-Json
        Require ($report.StartupPassed -is [bool] -and $report.StartupPassed) 'The native application did not confirm startup.'
        Require ($report.ConfiguredApiKey -is [bool] -and $report.ConfiguredApiKey -eq $Configured) 'The configured-key state did not match the scenario.'
        Require ($report.SetupRequired -is [bool] -and $report.SetupRequired -eq $Setup) 'The setup state did not match the scenario.'
        Require ($report.UiLanguage -ceq $Language) 'The startup UI language did not match the scenario.'
        # Retain only booleans in the reusable test's scenario reports.
        [ordered]@{
            StartupPassed=$true; ConfiguredApiKey=[bool]$report.ConfiguredApiKey;
            SetupRequired=[bool]$report.SetupRequired; ExpectedLanguage=$true;
            KeyValueAbsent=$true
        } | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(3000) }
            $process.Dispose()
        }
    }
}

try {
    foreach ($name in $prior.Keys) { Set-GamePulseProcessSetting $name $null }
    Invoke-StartupCheck 'without-file' $false $true 'en'

    [IO.File]::WriteAllText($ownedEnvironment,
        ("# Synthetic configuration used only by this isolated startup check.`nGAMEPULSE_STEAM_API_KEY=" + $placeholder + "`nGAMEPULSE_UI_LANGUAGE=uk`n"),$utf8)
    Invoke-StartupCheck 'working-directory-file' $true $false 'uk'

    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' '1'
    Invoke-StartupCheck 'file-loading-disabled' $false $true 'en'

    Set-GamePulseProcessSetting 'GAMEPULSE_DISABLE_DOTENV' $null
    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' 'en'
    Invoke-StartupCheck 'process-language-precedence' $true $false 'en'

    Set-GamePulseProcessSetting 'GAMEPULSE_UI_LANGUAGE' $null
    [IO.File]::WriteAllText($ownedEnvironment,"GAMEPULSE_UI_LANGUAGE=uk`nGAMEPULSE_STEAM_API_KEY='unterminated`n",$utf8)
    Invoke-StartupCheck 'malformed-file-setup' $false $true 'en'

    $result = [ordered]@{
        Passed=$true; NativePublishedStartup=$true; NoFileSetup=$true;
        WorkingDirectoryEnvironment=$true; FileLoadingOptOut=$true;
        ProcessLanguagePrecedence=$true; MalformedFileGracefulSetup=$true;
        KeyValuesAbsentFromReports=$true; ExistingEnvironmentFilesRead=$false;
        ExistingEnvironmentFilesModified=$false; PublishDirectoryModified=$false;
        ExecutableDirectoryFallbackTested=$false; LiveSteamRequestsTested=$false
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'local-environment-results.json') -Encoding UTF8
    Write-Host 'Five isolated native configuration startup checks passed; no live profile lookup was requested.'
    [pscustomobject]$result
}
finally {
    foreach ($name in $prior.Keys) { Set-GamePulseProcessSetting $name $prior[$name] }
    # Remove only the exact synthetic file created in the fresh, verified test directory.
    if (Test-Path -LiteralPath $ownedEnvironment -PathType Leaf) { Remove-Item -LiteralPath $ownedEnvironment }
}
