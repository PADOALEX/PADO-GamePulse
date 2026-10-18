[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
Assert-GamePulseOrdinaryPath $testRoot
if (Test-Path -LiteralPath $testRoot) { throw 'Choose a fresh test directory.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$passed = 0
function Require([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Require-Rejection([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Require $rejected 'The unsafe release path or payload was accepted.'
}

# Use a unique synthetic variable; never inspect any actual application credential.
$variable = 'GAMEPULSE_RELEASE_TEST_' + [Guid]::NewGuid().ToString('N')
try {
    Set-GamePulseProcessSetting $variable 'synthetic-setting'
    Require ([Environment]::GetEnvironmentVariable($variable,'Process') -ceq 'synthetic-setting') 'A nonempty setting was not applied.'
    Set-GamePulseProcessSetting $variable $null
    Require ($null -eq [Environment]::GetEnvironmentVariable($variable,'Process')) 'Null removal must produce absence, not an empty environment override.'
    $absent = [Environment]::GetEnvironmentVariable($variable,'Process')
    Set-GamePulseProcessSetting $variable 'temporary-setting'
    Set-GamePulseProcessSetting $variable $absent
    Require ($null -eq [Environment]::GetEnvironmentVariable($variable,'Process')) 'Restoring an absent setting created an empty override.'
    Set-GamePulseProcessSetting $variable ''
    $empty = [Environment]::GetEnvironmentVariable($variable,'Process')
    $supportsEmpty = [Environment]::Version.Major -ge 9
    Require ((-not $supportsEmpty -and $null -eq $empty) -or ($supportsEmpty -and $null -ne $empty -and $empty.Length -eq 0)) 'Explicit empty values must follow the host runtime environment semantics.'
    Set-GamePulseProcessSetting $variable 'temporary-setting'
    Set-GamePulseProcessSetting $variable $empty
    $restored = [Environment]::GetEnvironmentVariable($variable,'Process')
    Require ((-not $supportsEmpty -and $null -eq $restored) -or ($supportsEmpty -and $null -ne $restored -and $restored.Length -eq 0)) 'Restoring an explicit empty setting changed its semantics.'
}
finally { Set-GamePulseProcessSetting $variable $null }

Require ((ConvertTo-GamePulseReleaseVersion '1.0.0') -ceq '1.0.0') 'Stable release version parsing failed.'
Require ((ConvertTo-GamePulseReleaseVersion '1.0.0+0123456789abcdef') -ceq '1.0.0') 'Git build metadata must retain the stable release version.'
Require ((ConvertTo-GamePulseReleaseVersion '0.9.0+source.commit') -ceq '0.9.0') 'Dotted build metadata must retain the stable release version.'
foreach ($invalid in @('1.0.0-preview','1.0.0+bad..metadata','1.0.0.0','1.0.0+')) {
    Require-Rejection { ConvertTo-GamePulseReleaseVersion $invalid }
}

$clean = Join-Path $testRoot 'clean-publish'
New-Item -ItemType Directory -Path $clean | Out-Null
[IO.File]::WriteAllText((Join-Path $clean 'GamePulse.Desktop.exe'),'Synthetic payload, not an executable.')
Assert-GamePulsePublishPayload $clean
$passed++
foreach ($name in @('.env','.ENV.local','.env.example','credential.key','certificate.pfx','private.log','GamePulse.LiveChecks.dll','GamePulse.UnitTests.dll')) {
    $fixture = Join-Path $testRoot ('forbidden-file-' + $passed)
    New-Item -ItemType Directory -Path $fixture | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture $name),'Isolated synthetic bytes; no real credentials.')
    Require-Rejection { Assert-GamePulsePublishPayload $fixture }
}
foreach ($name in @('.git','.idea','.vs','obj','bin','TestResults','test-fixtures')) {
    $fixture = Join-Path $testRoot ('forbidden-directory-' + $passed)
    New-Item -ItemType Directory -Path (Join-Path $fixture $name) | Out-Null
    Require-Rejection { Assert-GamePulsePublishPayload $fixture }
}
$outside = Join-Path $testRoot 'outside'
$linkedPayload = Join-Path $testRoot 'linked-payload'
New-Item -ItemType Directory -Path $outside,$linkedPayload | Out-Null
$link = Join-Path $linkedPayload 'linked-directory'
New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
Require-Rejection { Assert-GamePulsePublishPayload $linkedPayload }
Require-Rejection { Assert-GamePulseOrdinaryPath (Join-Path $link 'new-child') }
$result = [ordered]@{
    Passed=$true; Cases=$passed; PowerShellVersion=$PSVersionTable.PSVersion.ToString();
    HostRuntimeVersion=[Environment]::Version.ToString(); AbsentEnvironmentRestored=$true;
    ExplicitEmptyEnvironmentSemantics=$true; UnsafePayloadRejected=$true; ReparseAncestorsRejected=$true;
    RealCredentialValuesRead=$false; NativeApplicationStarted=$false; GitBuildMetadataSupported=$true
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'release-support-results.json') -Encoding UTF8
Write-Host ('Release helpers: ' + $passed + ' isolated checks passed.')
[pscustomobject]$result
