# Publishes a GitHub Release for a version tag (run by the build workflow).
#
#   -Tag      The pushed tag, e.g. v2.3.0. Must match <Version> in Purge.csproj.
#   -DryRun   Build the zip, checksum and notes, but don't create the release.
#
# Expects publish/Purge.exe to exist already.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [switch] $DryRun
)
$ErrorActionPreference = 'Stop'

# The tag must match the project version, so the exe's --version agrees with the release.
$version = ([xml](Get-Content Purge/Purge.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($Tag -ne "v$version") {
    throw "Tag '$Tag' doesn't match the version in Purge.csproj ($version). Tag the release as v$version, or update <Version> first."
}

# Zip the exe, with a SHA-256 checksum for anyone who wants to verify the download.
New-Item -ItemType Directory -Force release | Out-Null
$zip = "release/Purge-$version-win-x64.zip"
Compress-Archive -Path publish/Purge.exe -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -NoNewline "$zip.sha256"

# Release notes: the README's "Changes in <major.minor>" section, plus download instructions.
$minor = ($version -split '\.')[0..1] -join '.'
$lines = Get-Content README.md
$start = [array]::IndexOf($lines, "## Changes in $minor")
$changes = @()
if ($start -ge 0) {
    foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -like '## *') { break }
        $changes += $line
    }
}
$notes = @"
## Download

Download **Purge-$version-win-x64.zip** below and extract ``Purge.exe``. It is a single file that runs on any
64-bit Windows PC; nothing else needs to be installed. Put it in a folder on your PATH to run ``purge`` from anywhere.

## Changes

$(($changes -join "`n").Trim())
"@
$notes | Set-Content release/notes.md

if ($DryRun) {
    Write-Host "Dry run: would create release $Tag with $zip"
    Get-Content release/notes.md
    return
}

gh release create $Tag $zip "$zip.sha256" --title "Purge $version" --notes-file release/notes.md --verify-tag
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
