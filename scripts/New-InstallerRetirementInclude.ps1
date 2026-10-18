[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$HistoryDirectory=(Join-Path $PSScriptRoot '..\installer\payload-history')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
$publishRoot=(Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$historyRoot=(Resolve-Path -LiteralPath $HistoryDirectory).Path
$target=[IO.Path]::GetFullPath($OutputPath)
Assert-GamePulsePublishPayload $publishRoot
Assert-GamePulseOrdinaryPath $historyRoot
Assert-GamePulseOrdinaryPath $target
function Assert-SafeRelativePath([string]$Path) {
    if ($Path -notmatch '^([A-Za-z0-9_.+@()-]+/)*[A-Za-z0-9_.+@()-]+$') { throw 'Payload history contains an unsafe relative file name.' }
    foreach ($part in $Path.Split('/')) {
        if ($part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or
            $part -match '^(?i:con|prn|aux|nul|com[0-9]|lpt[0-9])(\.|$)' -or
            $part -match '^(?i:\.env($|\.)|\.git$|\.idea$|\.vs$|\.venv$|TestResults$|test-fixtures$)') {
            throw 'Payload history contains a reserved or traversing file name.'
        }
    }
}
$current=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @(Get-ChildItem -LiteralPath $publishRoot -Recurse -Force -File)) {
    $relative=$file.FullName.Substring($publishRoot.Length+1).Replace('\','/')
    Assert-SafeRelativePath $relative
    [void]$current.Add($relative)
}
$retired=New-Object 'Collections.Generic.SortedDictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$manifests=@(Get-ChildItem -LiteralPath $historyRoot -Filter '*.json' -File | Sort-Object Name)
foreach ($manifestFile in $manifests) {
    Assert-GamePulseOrdinaryPath $manifestFile.FullName
    $manifest=Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.archiveSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid payload-history metadata.' }
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $relative=[string]$entry.path
        Assert-SafeRelativePath $relative
        if (-not $seen.Add($relative) -or $entry.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Duplicate payload-history path or invalid SHA256.' }
        if ($current.Contains($relative)) { continue }
        if (-not $retired.ContainsKey($relative)) { $retired.Add($relative,(New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal))) }
        [void]$retired[$relative].Add([string]$entry.sha256)
    }
}
$lines=New-Object 'Collections.Generic.List[string]'
$lines.Add('{ Generated from source-controlled release histories, never an installed manifest. }')
$lines.Add('procedure RegisterRetiredManagedResources;')
$lines.Add('begin')
foreach ($relative in $retired.Keys) { $lines.Add("  RegisterExtraCloseApplicationsResource(ExpandConstant('{app}\"+$relative.Replace('/','\')+"'));") }
$lines.Add('end;')
$lines.Add('')
$lines.Add('procedure RemoveRetiredManagedFiles;')
$lines.Add('begin')
foreach ($relative in $retired.Keys) {
    foreach ($hash in @($retired[$relative] | Sort-Object)) { $lines.Add("  RetireManagedFile('"+$relative.Replace('/','\')+"', '"+$hash+"');") }
}
$lines.Add('end;')
[IO.File]::WriteAllLines($target,$lines,(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ HistoryManifests=$manifests.Count; CurrentFiles=$current.Count; RetiredPaths=$retired.Count; IncludePath=$target }
