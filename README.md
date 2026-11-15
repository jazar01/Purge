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
   



# Download

Get the latest `Purge-<version>-win-x64.zip` from the
[Releases page](https://github.com/jazar01/Purge/releases/latest) and extract it
to a folder such as `C:\Tools\Purge`. It runs on any 64-bit Windows PC with
nothing else installed. The zip holds two programs:

* `Purge.exe`, the command-line tool. Put its folder on your PATH to run `purge` from anywhere.
* `PurgeWindow.exe`, which adds Purge to the Explorer right-click menu (see below).

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
| `--remove-empty-folders` | With `--recurse`, also remove subfolders the purge leaves empty. |
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
below it. Only files are purged; folders are not deleted (unless you add
`--remove-empty-folders`, below), even if their name matches the pattern.

Each folder is treated as its own set of files. For example, with
`-r -n 5 -d 30 "D:\Backups\*.bak"` and one subfolder per database, every
database folder keeps its own 5 newest backups. A folder with many recent files
never causes another folder's last backups to be deleted.

Folder junctions and symbolic links are not followed, so a purge can't loop or
reach outside the folder you named. If a subfolder can't be read (for example,
access denied), it is skipped with a warning, the rest of the purge continues,
and the exit code is 2.

### Removing emptied folders (`--remove-empty-folders`)

Add `--remove-empty-folders` (with `--recurse`) to also remove subfolders that
the purge leaves empty, for example monthly backup folders once their last
backup has aged out:

```
Purge -r -d 90 --remove-empty-folders "D:\Backups\*.bak"
```

* Only folders **this purge emptied** are removed: a file was purged there (or an
  emptied folder inside it was removed), and nothing at all is left in it.
  Folders that were already empty are kept, since they may be waiting for new
  files, and so is any folder holding other files or folders.
* The folder named in the FileSpec is never removed.
* Folders are removed deepest first, so a chain of emptied folders goes in one run.
* `--whatif` lists the folders that would be removed.
* A folder that can't be removed is reported, and the exit code is 2.

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

## Explorer right-click menu

Open `PurgeWindow.exe` and click **Add to menu**. This adds two commands to the
File Explorer right-click menu:

* **Purge files…** on a folder (or the empty space inside one) opens a window with
  the same choices as the command line: file pattern, files to always keep, days
  to keep, size limits, created or modified time, subfolders (and removing the
  ones left empty), overwrite passes and read-only files. **Preview** shows what would be purged; **Purge** stays disabled
  until you have previewed the current settings, and asks before deleting.
* **Securely delete…** on selected files and folders lists them (with how many
  files each folder holds), lets you choose the number of overwrite passes, and
  asks before deleting. Every file is overwritten and deleted; selected folders
  are then removed along with all their subfolders.

  Folder junctions and symbolic links inside a folder are removed as links only;
  what they point to is never touched. If something can't be deleted (a file in
  use, a read-only file without the box ticked), the rest is deleted, only the
  folders still holding it are kept, and the window lists each item left under
  its folder with the reason (hover for the full path). If the folder itself
  couldn't be removed, for example because a program or terminal has it open,
  its row says why. Double-click any of these rows to show it in File Explorer.
  Whole drives, the Windows and Program Files folders, `C:\Users` and your user
  profile folder can't be securely deleted (folders inside your profile can).

  "Purge files…" is different: it cleans up *inside* a folder by rules and never
  removes the folder you right-clicked, so a backup folder stays in place for the
  next backup.

On Windows 11 the commands are under **Show more options** (or Shift+right-click);
the short Windows 11 menu only lists signed, packaged apps.

The menu is added for your Windows user only and needs no administrator rights.
It runs `PurgeWindow.exe` from where it was when you added it, so if you move the
file, open it and click **Update menu**. **Remove from menu** takes the commands
out again. For scripted setups, `PurgeWindow.exe --add-to-explorer` and
`PurgeWindow.exe --remove-from-explorer` do the same without a window.

`PurgeWindow.exe` also has buttons to open both windows without the menu.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including "no files matched"). |
| 1 | Bad arguments, or the directory could not be read. Nothing was deleted. |
| 2 | One or more files could not be purged (read-only, in use, access denied), or with `--recurse` a subfolder could not be read. |

## Secure wipe

With `--security-level` greater than 0 (or **Overwrite passes** in the windows),
each file is overwritten that many times with cryptographically random bytes,
including any hidden NTFS alternate data streams. It is then truncated, renamed
to a random name, and deleted.

**How much does that protect?** On an ordinary hard disk, one pass destroys the
file's contents; more passes add time but no real protection. Beyond that, a
wipe only reaches the file as it is now, where it is now:

* **SSDs and flash drives** write each change to new memory cells and erase the
  old ones later, on their own schedule. No program can guarantee the old data is
  gone. The reliable protection is **full-disk encryption (BitLocker)**, turned
  on before the data was written: deleted data is then unreadable without the key.
* **Compressed, sparse and EFS-encrypted files**, and drives formatted with
  **ReFS**, may have the new data written somewhere else, leaving the old data in
  place.
* **Network drives** store the data on the server, which may keep snapshots.
* **Other copies** are untouched: Volume Shadow Copies and restore points,
  backups, OneDrive (or other cloud) version history and recycle bin, and the
  older versions many programs leave in free space each time they save.
* The file's **name** can remain in the NTFS change journal.

Purge checks for these and prints a note (or shows a ⚠ line in the Securely
delete window) when they apply, for example:

```
Purging (age 45 days): D:\Finance\2026-09 statement.pdf
   Note: Solid-state drive D: - SSDs and flash drives write to new memory cells and erase the old ones later, so overwriting can't guarantee the old data is gone. Full-disk encryption (BitLocker) is the reliable protection.
```

Notes about a drive are shown once per run. The notes don't stop the purge or
change the exit code. To clear old data from a hard disk's free space, run
`cipher /w:D:\` (Windows' built-in free-space wiper).

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
dotnet publish Purge -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -p:EnableCompressionInSingleFile=true -o publish
dotnet publish PurgeWindow -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -p:EnableCompressionInSingleFile=true -o publish
```

This makes `publish\Purge.exe` (about 37 MB) and `publish\PurgeWindow.exe`
(about 52 MB), each a single file that runs on any 64-bit Windows PC with nothing
else installed. The GitHub build produces the same files as a downloadable
artifact.

For a much smaller exe (under 1 MB) that needs the .NET 10 runtime installed,
use `--self-contained false` and leave out the compression option.

Note that `Purge\bin\...\Purge.exe` from a plain `dotnet build` is only a
launcher: it needs the `.dll` and `.json` files next to it.

## Making a release

1. Set `<Version>` in `Directory.Build.props` and add a `## Changes in <major.minor>`
   section to this README. Merge that to master.
2. Tag the merged commit and push the tag:

   ```
   git tag v2.4.0
   git push origin v2.4.0
   ```

The build then publishes a GitHub Release named "Purge 2.4.0" with the zipped exe,
a SHA-256 checksum, and the README's changes section as release notes. The build
fails without releasing if the tag doesn't match `<Version>`.

## Changes in 2.7.2

* Fixed: Securely delete could wipe a folder's contents but not remove the
  folder itself ("in use by another program"). Explorer starts Purge with the
  folder you are viewing as its working folder, so when that was the folder
  being deleted, Purge's own window kept it in use. Purge now moves out of it
  on startup.

## Changes in 2.7.1

* Securely delete: when a folder can't be fully removed, the window now lists
  every item left with a plain reason (such as "in use by another program"),
  including the folder itself and files added while it was deleting.
  Double-click a row to show it in File Explorer.

## Changes in 2.7

* New `--remove-empty-folders` option (with `--recurse`) removes subfolders the
  purge leaves empty. The Purge files window has a matching checkbox.

## Changes in 2.6

* **Securely delete…** now works on folders too: everything inside is overwritten
  and deleted, and the folder itself is removed. After updating, open
  `PurgeWindow.exe` and click **Update menu** to add it to the folder menu.

## Changes in 2.5

* Secure wipe also overwrites NTFS alternate data streams.
* Purge now warns when overwriting can't be relied on: SSDs, compressed, sparse
  and EFS-encrypted files, ReFS and network drives, and OneDrive folders.
* The README explains what a secure wipe does and doesn't protect against.

## Changes in 2.4

* New `PurgeWindow.exe` adds **Purge files…** and **Securely delete…** to the
  Explorer right-click menu, with windows for choosing the options.

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
