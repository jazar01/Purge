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
| `-d, --keep-days <n>` | Keep files newer than this many days. [default: 0] |
| `-s, -l, --security-level <n>` | Passes of random overwriting before deleting, 0-50. [default: 0] |
| `-t, --whatif` | Test mode: show what would be purged without deleting anything. |
| `-f, --force` | Force deletion of read-only files. |
| `-p, --prompt` | Ask before deleting each file: **Y**es, **N**o, **A**ll remaining, **Q**uit. Needs an interactive console. |
| `-r, --recurse` | Also purge matching files in all subfolders. Keep-number and keep-days apply to each folder separately. |
| `--min-size <size>` | Only consider files at least this big, e.g. `500`, `10KB`, `1.5MB`, `2GB`. |
| `--max-size <size>` | Only consider files at most this big. |
| `--age-by <modified\|created>` | Measure age from the file's modified time (default) or created time. |
| `--log <file>` | Append a timestamped record of this run to the file. The log file itself is never purged. |
| `--version` | Show version information. |
| `-?, -h, --help` | Show help and usage information. |

Example: keep 30 days of daily backups, but never fewer than 5:

```
Purge -d 30 -n 5 "D:\Backups\Finance Backup*.zip"
```

Run it with `--whatif` first to see what would be deleted.

## How files are chosen

* A file's age is measured from its last-modified time, or its created time
  with `--age-by created`.
* Files more than `--keep-days` days old are purged, except that the newest
  `--keep-number` files are always kept. Ordering uses exact timestamps, so this
  works correctly with several backups a day.
* Pattern matching is exact: `*.bak` matches `x.bak` but not `x.bakup`.
* Hidden and system files that match the pattern are included.

## Size limits (`--min-size`, `--max-size`)

Size limits narrow which files Purge looks at, just like the file pattern.
Files outside the limits are ignored completely: they are never purged and
don't count toward `--keep-number`. The limits include the given sizes.

Sizes are a number with an optional unit: `B`, `KB`, `MB`, `GB` or `TB`
(1 KB = 1024 bytes; `K`, `M`, `G`, `T` also work, and case doesn't matter).

For example, to keep 30 days of real backups while ignoring tiny, broken ones:

```
Purge -d 30 -n 5 --min-size 1MB "D:\Backups\*.bak"
```

and to clean up only the broken ones:

```
Purge -d 7 --max-size 1KB "D:\Backups\*.bak"
```

## Created vs. modified (`--age-by`)

By default a file's age comes from its last-modified time. With
`--age-by created` it comes from the time the file was created, and
`--keep-number` keeps the most recently created files.

Be careful with created times: Windows gives a file a new created time when it
is copied (for example, restored from another disk), while the modified time
is kept. A copied old backup therefore looks new with `--age-by created`.

## Subfolders (`--recurse`)

With `--recurse`, the pattern is applied in the given folder and in every folder
below it. Only files are purged; folders are never deleted, even if their name
matches the pattern.

Each folder is treated as its own set of files. For example, with
`-r -n 5 -d 30 "D:\Backups\*.bak"` and one subfolder per database, every
database folder keeps its own 5 newest backups. A folder with many recent files
never causes another folder's last backups to be deleted.

Folder junctions and symbolic links are not followed, so a purge can't loop or
reach outside the folder you named. If a subfolder can't be read (for example,
access denied), it is skipped with a warning, the rest of the purge continues,
and the exit code is 2.

## Log file (`--log`)

`--log <file>` keeps a record of each run, which is handy for scheduled tasks
where nobody sees the console. Everything Purge prints is also written to the
file:

* New lines are added to the end, so the file builds up a history of runs.
* Each line starts with the local date and time, and error lines are tagged `ERROR`.
* Each run starts with a `=== Purge <version> started:` line showing the command
  line, and ends with `=== Finished, exit code <n>`.
* With `--prompt`, the question and your answer are recorded.
* The log's folder is created if needed. If the log can't be opened, Purge stops
  with exit code 1 before deleting anything.
* The log file is never purged and doesn't count toward `--keep-number`, even if
  it matches the pattern.
* Mistakes in the command line itself (such as `-n -1`) are reported before the
  log is opened, so they only appear on the console.

```
2026-11-14 04:00:01  === Purge 2.3.0 started: Purge -d 30 -n 5 --log D:\Logs\purge.log D:\Backups\*.zip
2026-11-14 04:00:01  Purging (age 31 days): D:\Backups\Finance 2026-10-14.zip
2026-11-14 04:00:01  ERROR     Unable to purge D:\Backups\Finance 2026-10-13.zip - read-only (use --force)
2026-11-14 04:00:01  Files purged: 1   Files retained: 30   Failed: 1   0.0 seconds
2026-11-14 04:00:01  === Finished, exit code 2
```

The log keeps growing; delete or archive it from time to time, or keep logs in
a dated file name chosen by the script that runs Purge.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including "no files matched"). |
| 1 | Bad arguments, or the directory could not be read. Nothing was deleted. |
| 2 | One or more files could not be purged (read-only, in use, access denied), or with `--recurse` a subfolder could not be read. |

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

## Changes in 2.3

* New `--min-size` and `--max-size` options to only consider files in a size range.
* New `--age-by created` option to measure age from the created time.

## Changes in 2.2

* New `--log <file>` option that appends a timestamped record of each run.

## Changes in 2.1

* New `-r, --recurse` option to purge matching files in subfolders, with
  keep-number and keep-days applied per folder.

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
