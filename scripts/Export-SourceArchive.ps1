[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Windows PowerShell 5.1 can bind -File parameter defaults before $PSScriptRoot is
# initialized. Resolve the optional source only after entering the script body.
if (-not $PSBoundParameters.ContainsKey('SourceDirectory')) {
    $SourceDirectory = Join-Path $PSScriptRoot '..'
}
Add-Type -AssemblyName System.IO.Compression

function Assert-OrdinaryAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Archive paths must not pass through junctions or symbolic links.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-SafeEnvironmentExample([string]$Path) {
    # Validate and archive the same bytes, even if an editor later changes the local template.
    if ((Get-Item -LiteralPath $Path -Force).Length -gt 16384) { throw 'The safe environment template must not exceed 16 KiB.' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $decoder = New-Object Text.UTF8Encoding($false,$true)
    $text = $decoder.GetString($bytes).TrimStart([char]0xFEFF)
    $keyCount = 0
    $languageCount = 0
    foreach ($line in ($text -split '\r\n|\n|\r')) {
        $value = $line.Trim()
        if (-not $value) { continue }
        if ($value.StartsWith('#')) {
            if ($value -match '[A-Fa-f0-9]{32,}' -or $value -match '(?i)(?:API_?KEY|TOKEN|SECRET)\s*=\s*[^\s#]') {
                throw 'The environment example contains a possible credential in a comment.'
            }
            continue
        }
        if ($value -cmatch '^GAMEPULSE_STEAM_API_KEY=\s*$') { $keyCount++; continue }
        if ($value -cmatch '^GAMEPULSE_UI_LANGUAGE=(en|uk)\s*$') { $languageCount++; continue }
        throw 'The environment example must contain only an empty GAMEPULSE_STEAM_API_KEY and an en/uk UI language.'
    }
    if ($keyCount -ne 1 -or $languageCount -ne 1) { throw 'The environment example requires exactly one empty API key and one en/uk UI language.' }
    return ,$bytes
}

$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\','/')
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
Assert-OrdinaryAncestors $sourceRoot
Assert-OrdinaryAncestors $outputRoot
if (-not (Get-Item -LiteralPath $sourceRoot -Force).PSIsContainer) { throw 'The source path must be a directory.' }
$sourcePrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
if ($outputRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Select an archive output directory outside the source tree.'
}
$propertiesPath = Join-Path $sourceRoot 'Directory.Build.props'
Assert-OrdinaryAncestors $propertiesPath
[xml]$properties = [IO.File]::ReadAllText($propertiesPath)
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A numeric three-part source version is required.' }
$archiveName = 'PADO.GamePulse-' + $version + '-source.zip'
$target = Join-Path $outputRoot $archiveName
$checksum = $target + '.sha256'
if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $checksum)) { throw 'Source archive or checksum already exists; choose a fresh output directory.' }

$excludedDirectories = @('bin','obj','.git','.vs','.idea','.vscode','TestResults','artifacts','.venv','venv','__pycache__','node_modules')
$excludedExtensions = @('.pfx','.p12','.pem','.key','.crt','.cer','.exe','.dll','.zip','.7z','.tar','.gz','.user','.suo','.log','.dmp','.pyc')
$files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
$safeExamples = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]'
$pending = New-Object 'System.Collections.Generic.Stack[string]'
$pending.Push($sourceRoot)
$excludedCount = 0
while ($pending.Count) {
    $directory = $pending.Pop()
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        # Names/attributes are checked before opening content. Actual .env files are never read.
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $excludedCount++; continue }
        if ($entry.PSIsContainer) {
            if ($excludedDirectories -icontains $entry.Name -or $entry.Name -ilike '.env*') { $excludedCount++; continue }
            $pending.Push($entry.FullName)
            continue
        }
        if (($entry.Name -ilike '.env*' -and $entry.Name -cne '.env.example') -or
            $excludedExtensions -icontains $entry.Extension -or
            $entry.Name -iin @('local.settings.json','.DS_Store','Thumbs.db')) { $excludedCount++; continue }
        if ($entry.Name -ceq '.env.example') { $safeExamples.Add($entry.FullName,(Assert-SafeEnvironmentExample $entry.FullName)) }
        $files.Add($entry)
    }
}
if (-not ($files | Where-Object { $_.FullName -eq (Join-Path $sourceRoot '.env.example') })) { throw 'A safe root .env.example is required.' }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Assert-OrdinaryAncestors $outputRoot
$stream = New-Object IO.FileStream($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($file in @($files | Sort-Object FullName)) {
            Assert-OrdinaryAncestors $file.FullName
            $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\','/')
            if (-not $relative -or $relative.Split('/') -contains '..' -or $relative -match '^[/\\]|:') { throw 'Unsafe source archive entry path.' }
            $entry = $archive.CreateEntry(('PADO.GamePulse/' + $relative),[IO.Compression.CompressionLevel]::Optimal)
            if ($safeExamples.ContainsKey($file.FullName)) {
                $destination = $entry.Open()
                try {
                    $exampleBytes = $safeExamples[$file.FullName]
                    $destination.Write($exampleBytes,0,$exampleBytes.Length)
                } finally { $destination.Dispose() }
                continue
            }
            $input = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $destination = $entry.Open()
                try { $input.CopyTo($destination) } finally { $destination.Dispose() }
            } finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
$sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksum, ($sha256 + '  ' + $archiveName + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
$result = [ordered]@{ Version=$version; Archive=$target; Sha256=$sha256; Files=$files.Count; ExcludedEntries=$excludedCount; ActualEnvironmentFilesRead=$false }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot ('source-archive-' + $version + '.json')) -Encoding UTF8
Write-Host ('Source archive: ' + $target)
[pscustomobject]$result
