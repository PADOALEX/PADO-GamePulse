[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$TestDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
$testRoot=[IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $testRoot) { throw 'Select a fresh test directory; existing files are never removed.' }
$publish=Join-Path $testRoot 'publish'
$history=Join-Path $testRoot 'history'
New-Item -ItemType Directory -Path $publish,$history | Out-Null
$output=Join-Path $testRoot 'generated.iss'
$manifest=Join-Path $history '0.7.0.json'
$generator=Join-Path $PSScriptRoot 'New-InstallerRetirementInclude.ps1'
$hash='a'*64
$passed=0
function Set-History([object[]]$Entries) {
    @{ schemaVersion=1; version='0.7.0'; archiveSha256=('b'*64); files=$Entries } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding UTF8
}
function Generate { & $generator -PublishDirectory $publish -OutputPath $output -HistoryDirectory $history }
function Require([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Require-Rejection([object[]]$Entries) {
    Set-History $Entries
    $rejected=$false
    try { $null=Generate } catch { $rejected=$true }
    Require $rejected 'An unsafe payload history was accepted.'
    $script:passed++
}
Set-History @(@{path='old.dll';sha256=$hash},@{path='third-party-notices/old.txt';sha256=$hash})
$generated=Generate
Require ($generated.RetiredPaths -eq 2) 'Known absent files were not included.'
$content=[IO.File]::ReadAllText($output)
Require ($content.Contains("RetireManagedFile('old.dll', '"+$hash+"');")) 'The generated hash/path call was incorrect.'
$passed++
[IO.File]::WriteAllText((Join-Path $publish 'OLD.dll'),'New payload takes precedence regardless of a former hash.')
$generated=Generate
Require ($generated.RetiredPaths -eq 1) 'A current payload path could be deleted by retirement (case insensitive).'
$passed++
foreach ($unsafe in @('../outside.dll','nested/../../outside.dll','/absolute.dll','C:/absolute.dll','folder//file.dll','folder/./file.dll','folder/file.dll.','folder/CON.txt','old'';DeleteFile(''x'').dll','{app}/file.dll','folder\file.dll','file?.dll','.env','nested/.env.local','.git/config','nested/.idea/settings.xml')) {
    Require-Rejection @(@{path=$unsafe;sha256=$hash})
}
Require-Rejection @(@{path='old.dll';sha256='not-a-sha256'})
Require-Rejection @(@{path='old.dll';sha256=$hash},@{path='OLD.dll';sha256=$hash})
$result=[ordered]@{Passed=$true; Cases=$passed; KnownRetirement=$true; CurrentPathWins=$true; MalformedAndTraversingPathsRejected=$true; InvalidHashAndDuplicateRejected=$true; NativeDeletionTested=$false}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'retirement-generator-results.json') -Encoding UTF8
Write-Host ('Installer retirement generator: '+$passed+' checks passed (no installation performed).')
