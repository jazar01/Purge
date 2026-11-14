## Advanced file deletion

Utility to delete files with some advanced features.

for Windows operating systems

This utility was initially designed to delete backup files that are no longer needed.
It is designed as a command line utility to be run as part of a script or scheduled
task. There is no reason why it wouldn't be good for managing other files that grow
in numbers over time, like log files for example.

If you have a backup scheduled on a certain frequency, say "Daily".  Lets also say the
backup file is zipped and has a name that includes the date, something like this:
"Finance Backup September 25, 2020 04:00.zip".    These files accumulate on a network or
external drive until deleted.  The standard delete commands usually have a parameter 
that allows for deleting files older than a certain date or even a number of days old. 
 

But what if you want to make sure you have at least 5 previous versions on hand at all 
times, in addition to a number of days.  For example; I want to keep 30 days worth of
daily backups, but under no circumstances do I want to have less than 5 backups.  Lets 
say your backup job starts failing and you didn't realize it.  After 30 days, all of your
backups would be deleted. 

The Purge utility provides the functionality to specify a minimum number of backups in 
addition to a number of days to retain.  It will only delete files in a specified directory 
and filename pattern, if they meet the age AND number criteria.

The Purge utility also has the option to securely wipe files as they are deleted by 
overwriting the file with random bytes.  This is useful for wiping sensitive log files
or backup files.

Future consideration:  Send an alert via email if no new files are found for x days.
   While this sounds like a good idea, it might be better off in a separate utility.
   



# Usage

```
Purge <FileSpec> [options]
```

| Argument / option | Description |
|---|---|
| `<FileSpec>` | Directory and file pattern of the files to purge, e.g. `D:\Backups\Finance*.zip`. Wildcards (`*`, `?`) are allowed in the file name but not in the directory. A bare directory is rejected. |
| `-n, --keep-number <n>` | Always keep at least this many of the newest files. [default: 0] |
| `-d, --keep-days <n>` | Keep files modified within this many days. [default: 0] |
| `-s, -l, --security-level <n>` | Passes of random overwriting before deleting, 0-50. [default: 0] |
| `-t, --whatif` | Test mode: show what would be purged without deleting anything. |
| `-f, --force` | Force deletion of read-only files. |
| `-p, --prompt` | Ask before deleting each file: **Y**es, **N**o, **A**ll remaining, **Q**uit. Needs an interactive console. |
| `--version` | Show version information. |
| `-?, -h, --help` | Show help and usage information. |

Example: keep 30 days of daily backups, but never fewer than 5:

```
Purge -d 30 -n 5 "D:\Backups\Finance Backup*.zip"
```

Run it with `--whatif` first to see what would be deleted.

## How files are chosen

* A file's age is measured from its last-modified time.
* Files more than `--keep-days` days old are purged, except that the newest
  `--keep-number` files are always kept. Ordering uses exact timestamps, so this
  works correctly with several backups a day.
* Pattern matching is exact: `*.bak` matches `x.bak` but not `x.bakup`.
* Hidden and system files that match the pattern are included.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including "no files matched"). |
| 1 | Bad arguments, or the directory could not be read. Nothing was deleted. |
| 2 | One or more files could not be purged (read-only, in use, access denied). |

## Secure wipe

With `--security-level` greater than 0, each file is overwritten that many times
with cryptographically random bytes, truncated, renamed to a random name, and then
deleted. This is best effort: SSDs, copy-on-write or compressed file systems, and
Volume Shadow Copies may keep old data elsewhere on the disk.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
dotnet publish Purge -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
```

Use `--self-contained true` for an exe that runs without the .NET runtime
installed (larger file).

## Changes in 2.0

* Ported from .NET Framework 4.7.2 to .NET 10 and the released System.CommandLine 2.0.
  The project now builds from a clean clone.
* Fixed: `*.zip` style patterns also matched longer extensions such as `.zipx`.
* Fixed: `--keep-number` could keep the wrong files when several had the same age in days.
* Fixed: the exit code was always 0; errors now return 1 or 2.
* Fixed: `--prompt` crashed when input was redirected, and a `--force` error could stop the run.
* Fixed: failed files are counted, and `--whatif` reports what would be purged.
* Ages are compared exactly rather than in whole days, so `-d 0` now includes files
  less than a day old.
* Faster, stronger wipe (crypto random, one flush per pass, file renamed before delete).
* `-s` now works as documented (alias of `-l`), and `--prompt` gains All and Quit.
* Unit tests and a GitHub Actions build.
