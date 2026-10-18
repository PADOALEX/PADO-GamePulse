# Shared release helpers. No environment values are logged or persisted.
function ConvertTo-GamePulseReleaseVersion([string]$ProductVersion) {
    # A build from a Git checkout can append +SourceRevisionId to InformationalVersion.
    # Keep the stable three-part release comparison without rejecting legitimate build metadata.
    if ($ProductVersion.Length -gt 128 -or $ProductVersion -notmatch '\A(?<Release>[0-9]+\.[0-9]+\.[0-9]+)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') {
        throw 'The executable has an unsupported release version.'
    }
    try { return ([Version]$Matches.Release).ToString(3) }
    catch { throw 'The executable has an invalid release version.' }
}

function Set-GamePulseProcessSetting([string]$Name,[AllowNull()][AllowEmptyString()][object]$Value) {
    # PowerShell 7 can bind $null as String.Empty. Since .NET 9, an explicitly empty
    # process variable is preserved and intentionally overrides a local .env value.
    # NullString asks the method binder for an actual null, preserving absence.
    if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name,[NullString]::Value,'Process') }
    else { [Environment]::SetEnvironmentVariable($Name,[string]$Value,'Process') }
}

function Assert-GamePulseOrdinaryPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Release paths must not pass through junctions or symbolic links.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-GamePulsePublishPayload([string]$Path) {
    Assert-GamePulseOrdinaryPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw 'The publish directory is missing.' }
    # Traverse explicitly; reject a link before enumerating through it.
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Publish payloads must contain ordinary files and directories only.'
            }
            if ($entry.Name -match '^(\.env($|\.)|\.venv$|\.idea$|\.git$|\.vs$|obj$|bin$|TestResults$|test-fixtures$|GamePulse\.(UnitTests|IntegrationTests|UiChecks|LiveChecks))' -or
                (-not $entry.PSIsContainer -and $entry.Extension -iin @('.pfx','.p12','.pem','.key','.log','.dmp'))) {
                throw 'A forbidden credential, test, IDE or build artifact reached the publish directory.'
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}
