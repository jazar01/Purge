# Recreates a throwaway sample folder for the debug configurations in launch.json,
# so debugging never touches real files. Every run deletes and rebuilds it.
#
#   %TEMP%\PurgeDebug\
#     Backup-01.zip ... Backup-08.zip   one file per 10 days of age (0, 10, ... 70 days old)
#     Notes.txt, Old notes.txt          a current and a 90-day-old text file
#     Reports\Report-1.txt ... -3.txt   old files in a subfolder (for --recurse)
#     Reports\Empty after purge\Draft.txt   a folder the purge would leave empty
#     Wipe me\Secret.txt, Secret 2.txt  files for "Securely delete files"
#     Wipe me\Folder to wipe\...        a folder for "Securely delete a folder"

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP 'PurgeDebug'

if (Test-Path $root) { Remove-Item $root -Recurse -Force }

function New-Sample([string]$relative, [int]$daysOld, [int]$kb = 4) {
    $path = Join-Path $root $relative
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $bytes = New-Object byte[] ($kb * 1KB)
    [System.Random]::new().NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($path, $bytes)
    $when = (Get-Date).AddDays(-$daysOld)
    (Get-Item $path).CreationTime = $when
    (Get-Item $path).LastWriteTime = $when
}

for ($i = 1; $i -le 8; $i++) { New-Sample ("Backup-{0:D2}.zip" -f $i) (($i - 1) * 10) 64 }
New-Sample 'Notes.txt' 0
New-Sample 'Old notes.txt' 90
for ($i = 1; $i -le 3; $i++) { New-Sample "Reports\Report-$i.txt" (30 + $i * 10) }
New-Sample 'Reports\Empty after purge\Draft.txt' 120
New-Sample 'Wipe me\Secret.txt' 0
New-Sample 'Wipe me\Secret 2.txt' 0
New-Sample 'Wipe me\Folder to wipe\Inside.txt' 0
New-Sample 'Wipe me\Folder to wipe\Deeper\Inside 2.txt' 0

Write-Host "Sample files ready in $root"
