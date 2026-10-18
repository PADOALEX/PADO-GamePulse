[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$TestDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $testRoot) { throw 'Choose a fresh test directory; existing files are never removed.' }
$source = Join-Path $testRoot 'fixture-source'
$output = Join-Path $testRoot 'archive-output'
$exporter = Join-Path $PSScriptRoot 'Export-SourceArchive.ps1'
New-Item -ItemType Directory -Path $source | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-Fixture([string]$Relative,[string]$Content) {
    $path = Join-Path $source $Relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Content,$utf8)
}
function Require([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Require-Rejection([scriptblock]$Action,[string]$Message) {
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Require $rejected $Message
}
$sentinel = 'TEST_ONLY_CREDENTIAL_DO_NOT_EXPORT_3179'
Write-Fixture 'Directory.Build.props' '<Project><PropertyGroup><Version>0.9.0</Version></PropertyGroup></Project>'
Write-Fixture 'src/Example.cs' 'public sealed class Example { }'
$safeExample = "# Local setup template; never commit a credential.`nGAMEPULSE_STEAM_API_KEY=`nGAMEPULSE_UI_LANGUAGE=uk`n"
Write-Fixture '.env.example' $safeExample
foreach ($name in @('.env','.env.local','.env.production','src/.env','src/.env.user','src/.ENV','bin/private.txt','obj/private.txt','.git/private.txt','.vs/private.txt','.idea/private.txt','.vscode/private.txt','TestResults/private.txt','artifacts/private.txt','.venv/private.txt','venv/private.txt','__pycache__/private.txt','node_modules/private.txt','src/secret.pfx','src/secret.pem','src/secret.key','src/build.exe','src/build.dll','src/old.zip','local.settings.json')) {
    Write-Fixture $name $sentinel
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../.gitignore') -Destination (Join-Path $source '.gitignore')
$outside = Join-Path $testRoot 'outside-fixture'
New-Item -ItemType Directory -Path $outside | Out-Null
[IO.File]::WriteAllText((Join-Path $outside 'private.txt'),$sentinel,$utf8)
$junction = Join-Path $source 'linked-secrets'
New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
$exported = & $exporter -SourceDirectory $source -OutputDirectory $output
Require ($exported.ActualEnvironmentFilesRead -eq $false) 'The exporter must not read actual environment files.'
$read = [IO.File]::OpenRead($exported.Archive)
$entryNames = @()
$contents = New-Object 'System.Collections.Generic.List[string]'
try {
    $archive = New-Object IO.Compression.ZipArchive($read,[IO.Compression.ZipArchiveMode]::Read,$true)
    try {
        foreach ($entry in $archive.Entries) {
            Require ($entry.FullName.StartsWith('PADO.GamePulse/') -and $entry.FullName -notmatch '\\|:|(^|/)\.\.(/|$)') 'Archive entry paths must be safe and contained.'
            $entryNames += $entry.FullName
            $entryStream = $entry.Open()
            try {
                $reader = New-Object IO.StreamReader($entryStream)
                try { $contents.Add($reader.ReadToEnd()) } finally { $reader.Dispose() }
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $read.Dispose() }
Require ($entryNames -contains 'PADO.GamePulse/src/Example.cs') 'The source file must be retained.'
Require ($entryNames -contains 'PADO.GamePulse/.env.example') 'The safe environment example must be retained.'
Require (@($entryNames | Where-Object { $_ -match '(^|/)\.env' -and $_ -cne 'PADO.GamePulse/.env.example' }).Count -eq 0) 'Actual environment files leaked into the source archive.'
Require (@($contents | Where-Object { $_.Contains($sentinel) }).Count -eq 0) 'Excluded credential/build fixture bytes leaked into the archive.'
Require ($exported.Files -eq 4) 'Only source, properties, gitignore and safe example should be archived.'
$digest = (Get-FileHash -LiteralPath $exported.Archive -Algorithm SHA256).Hash.ToLowerInvariant()
Require ($digest -eq $exported.Sha256) 'The archive SHA256 must match its report.'
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory $output } 'An existing source archive must never be overwritten.'
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory (Join-Path $source 'artifacts') } 'Source-local archive output must be rejected.'
Require-Rejection { & $exporter -SourceDirectory $junction -OutputDirectory (Join-Path $testRoot 'unsafe-source-link') } 'Source directories reached through junctions must be rejected.'
$outputJunction = Join-Path $testRoot 'linked-output'
New-Item -ItemType Junction -Path $outputJunction -Target $outside | Out-Null
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory $outputJunction } 'Output directories reached through junctions must be rejected.'
Write-Fixture '.env.example' ("GAMEPULSE_STEAM_API_KEY=" + $sentinel + "`nGAMEPULSE_UI_LANGUAGE=en`n")
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory (Join-Path $testRoot 'unsafe-key') } 'A nonempty API key in the example must be rejected.'
Write-Fixture '.env.example' "GAMEPULSE_STEAM_API_KEY=`nGAMEPULSE_UI_LANGUAGE=en`nUNSUPPORTED_TOKEN=fixture`n"
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory (Join-Path $testRoot 'unsafe-setting') } 'Unknown assignments in the example must be rejected.'
Write-Fixture '.env.example' "# API_KEY=fixture`nGAMEPULSE_STEAM_API_KEY=`nGAMEPULSE_UI_LANGUAGE=en`n"
Require-Rejection { & $exporter -SourceDirectory $source -OutputDirectory (Join-Path $testRoot 'unsafe-comment') } 'Credential assignments in example comments must be rejected.'
Write-Fixture '.env.example' $safeExample
$gitChecked = $false
if (Get-Command git -ErrorAction SilentlyContinue) {
    & git -C $source init --quiet
    Require ($LASTEXITCODE -eq 0) 'Isolated Git fixture initialization failed.'
    foreach ($path in @('.env','.env.local','.env.production','src/.env','src/.env.user')) {
        & git -C $source check-ignore --quiet -- $path
        Require ($LASTEXITCODE -eq 0) 'The copied gitignore must ignore local environment files at every depth.'
    }
    & git -C $source check-ignore --quiet -- '.env.example'
    Require ($LASTEXITCODE -eq 1) 'The safe environment example must remain eligible for Git.'
    $gitChecked = $true
}
# Exercise the documented standalone command without -SourceDirectory. Calling
# the exporter with & and an explicit fixture path does not reproduce PS 5.1's
# early -File parameter binding, so use child hosts and an unrelated working dir.
$standaloneSource = Join-Path $testRoot 'standalone-source'
$standaloneScripts = Join-Path $standaloneSource 'scripts'
$standaloneWorkingDirectory = Join-Path $testRoot 'standalone-working-directory'
New-Item -ItemType Directory -Path $standaloneScripts,$standaloneWorkingDirectory | Out-Null
foreach ($relative in @('Directory.Build.props','.env.example','src/Example.cs')) {
    $target = Join-Path $standaloneSource $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $target
}
[IO.File]::WriteAllText((Join-Path $standaloneSource '.env'),$sentinel,$utf8)
$standaloneExporter = Join-Path $standaloneScripts 'Export-SourceArchive.ps1'
Copy-Item -LiteralPath $exporter -Destination $standaloneExporter
$standaloneHosts = [ordered]@{ WindowsPowerShell='powershell.exe'; PowerShell7='pwsh.exe' }
$standaloneResults = @{}
foreach ($hostName in $standaloneHosts.Keys) {
    $hostCommand = Get-Command $standaloneHosts[$hostName] -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $hostCommand) {
        if ($hostName -eq 'WindowsPowerShell') { throw 'Windows PowerShell is required for the standalone source archive regression.' }
        $standaloneResults[$hostName] = $false
        continue
    }
    $standaloneOutput = Join-Path $testRoot ('standalone-output-' + $hostName)
    # Invoke the native host directly, as documented. PowerShell adjusts Windows
    # PowerShell's module path when called from PS 7; Start-Process bypasses that
    # compatibility handling and can prevent built-in cmdlets from autoloading.
    Push-Location -LiteralPath $standaloneWorkingDirectory
    try {
        & $hostCommand.Source -NoProfile -ExecutionPolicy Bypass -File $standaloneExporter -OutputDirectory $standaloneOutput `
            1> (Join-Path $testRoot ($hostName + '-standalone.stdout.txt')) `
            2> (Join-Path $testRoot ($hostName + '-standalone.stderr.txt'))
        $standaloneExitCode = $LASTEXITCODE
    } finally { Pop-Location }
    Require ($standaloneExitCode -eq 0) ($hostName + ' standalone -File default-source export failed; inspect its isolated stderr log.')
    $standaloneReportPath = Join-Path $standaloneOutput 'source-archive-0.9.0.json'
    Require (Test-Path -LiteralPath $standaloneReportPath -PathType Leaf) ($hostName + ' did not resolve the source beside the copied exporter.')
    $standaloneReport = [IO.File]::ReadAllText($standaloneReportPath) | ConvertFrom-Json
    Require ($standaloneReport.ActualEnvironmentFilesRead -eq $false) ($hostName + ' must exclude local environment files before opening content.')
    $standaloneArchivePath = Join-Path $standaloneOutput 'PADO.GamePulse-0.9.0-source.zip'
    Require ($standaloneReport.Archive -eq $standaloneArchivePath) ($hostName + ' reported an unexpected archive path.')
    $standaloneRead = [IO.File]::OpenRead($standaloneArchivePath)
    try {
        $standaloneArchive = New-Object IO.Compression.ZipArchive($standaloneRead,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            Require ($null -ne $standaloneArchive.GetEntry('PADO.GamePulse/src/Example.cs')) ($hostName + ' did not archive the isolated source fixture.')
            Require ($null -eq $standaloneArchive.GetEntry('PADO.GamePulse/.env')) ($hostName + ' included a private environment file.')
            Require ($standaloneArchive.Entries.Count -eq 4) ($hostName + ' archived unexpected files outside the isolated source fixture.')
        } finally { $standaloneArchive.Dispose() }
    } finally { $standaloneRead.Dispose() }
    $standaloneResults[$hostName] = $true
}
$result = [ordered]@{ Passed=$true; ExclusionFixtures=25; SafeEntryPaths=$true; CredentialBytesAbsent=$true; Sha256Verified=$true; UnsafeExampleRejected=$true; ReparseEntriesExcluded=$true; ReparseRootsRejected=$true; NoOverwrite=$true; OutputOutsideSource=$true; GitIgnoreChecked=$gitChecked; WindowsPowerShellStandaloneDefault=$standaloneResults.WindowsPowerShell; PowerShell7StandaloneDefault=$standaloneResults.PowerShell7 }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'source-archive-test-results.json') -Encoding UTF8
Write-Host 'Source archive safety checks passed; only isolated dummy credentials were used.'
[pscustomobject]$result
