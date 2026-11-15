using System.Diagnostics;

namespace Purge;

public sealed record PurgeOptions(
    string FileSpec,
    int KeepNumber = 0,
    int KeepDays = 0,
    int SecurityLevel = 0,
    bool WhatIf = false,
    bool Force = false,
    bool Prompt = false,
    bool Recurse = false,
    string? ExcludeFile = null,
    FileSelection? Selection = null);

/// <summary>Process exit codes, so scripts and scheduled tasks can detect problems.</summary>
public static class ExitCodes
{
    /// <summary>Everything that should have been purged was purged (or nothing matched).</summary>
    public const int Success = 0;

    /// <summary>Bad arguments, or the file list could not be read. Nothing was deleted.</summary>
    public const int Error = 1;

    /// <summary>One or more files could not be purged, or (with --recurse) a subfolder could not be read.</summary>
    public const int PartialFailure = 2;
}

/// <summary>
/// Purges the files selected by <see cref="PurgeOptions"/>.
/// </summary>
public sealed class PurgeProcessor
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly Func<char>? _readKey;

    /// <param name="output">Where progress is written.</param>
    /// <param name="error">Where errors are written.</param>
    /// <param name="readKey">Reads one key press for --prompt; null when no interactive console is available.</param>
    public PurgeProcessor(TextWriter output, TextWriter error, Func<char>? readKey = null)
    {
        _out = output;
        _err = error;
        _readKey = readKey;
    }

    /// <summary>Files purged (or, with --whatif, that would be purged) by the last <see cref="Run"/>.</summary>
    public int Purged { get; private set; }

    /// <summary>Files kept by the last <see cref="Run"/>.</summary>
    public int Retained { get; private set; }

    /// <summary>Files that could not be purged in the last <see cref="Run"/>.</summary>
    public int Failed { get; private set; }

    public int Run(PurgeOptions options, DateTime? nowUtc = null)
    {
        var stopwatch = Stopwatch.StartNew();
        Purged = Retained = Failed = 0;

        if (options.Prompt && !options.WhatIf && _readKey is null)
        {
            _err.WriteLine("Error: --prompt needs an interactive console (input is redirected).");
            return ExitCodes.Error;
        }

        var selection = options.Selection ?? FileSelection.Default;
        int folderErrors = 0;
        IReadOnlyList<FileList> folders;
        try
        {
            folders = FileList.FromFileSpec(options.FileSpec, nowUtc ?? DateTime.UtcNow, options.Recurse,
                onFolderError: (folder, e) =>
                {
                    _err.WriteLine($"   Skipping folder {folder.FullName} - {e.Message}");
                    folderErrors++;
                },
                selection: selection);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _err.WriteLine("Error: " + e.Message);
            return ExitCodes.Error;
        }

        // Never purge the excluded file (the log), and don't let it count toward keep-number.
        if (options.ExcludeFile is { } exclude)
        {
            string excludePath = Path.GetFullPath(exclude);
            folders = folders
                .Select(f => new FileList(f.Items.Where(e =>
                    !string.Equals(e.File.FullName, excludePath, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        if (folders.All(f => f.Items.Count == 0))
        {
            _out.WriteLine($"No files match {options.FileSpec}" +
                           (options.Recurse ? " in any subfolder" : "") +
                           (selection.MinSize is not null || selection.MaxSize is not null ? " within the size limits" : ""));
            return folderErrors > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
        }

        // Keep-number and keep-days apply to each folder separately.
        foreach (var folder in folders)
            folder.MarkFilesForDeletion(options.KeepNumber, options.KeepDays);

        int purged = 0, retained = 0, failed = 0;
        bool confirmAll = false, quit = false;
        var notedDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string verb = options.WhatIf ? "Would purge" : "Purging";

        foreach (var entry in folders.SelectMany(f => f.Items))
        {
            if (!entry.IsCandidate || quit)
            {
                retained++;
                continue;
            }

            string age = selection.AgeBy == AgeBy.Created ? $"created {entry.Age} days ago" : $"age {entry.Age} days";
            _out.WriteLine($"{verb} ({age}): {entry.File.FullName}");

            // Say when overwriting can't be relied on. Drive-wide notes are shown once.
            if (options.SecurityLevel > 0)
            {
                foreach (var warning in WipeCheck.For(entry.File))
                {
                    if (!warning.AppliesToDrive || notedDrives.Add(warning.Title))
                        _out.WriteLine($"   Note: {warning}");
                }
            }

            if (options.WhatIf)
            {
                purged++;
                continue;
            }

            if (options.Prompt && !confirmAll)
            {
                switch (Ask())
                {
                    case 'n':
                        _out.WriteLine("   Skipped.");
                        retained++;
                        continue;
                    case 'q':
                        _out.WriteLine("   Quitting; remaining files are kept.");
                        quit = true;
                        retained++;
                        continue;
                    case 'a':
                        confirmAll = true;
                        break;
                }
            }

            try
            {
                if (options.Force && entry.File.IsReadOnly)
                    entry.File.IsReadOnly = false;

                FileWiper.WipeAndDelete(entry.File, options.SecurityLevel);
                purged++;
            }
            catch (UnauthorizedAccessException)
            {
                entry.File.Refresh();
                _err.WriteLine(entry.File.IsReadOnly
                    ? $"   Unable to purge {entry.File.FullName} - read-only (use --force)"
                    : $"   Unable to purge {entry.File.FullName} - access denied");
                failed++;
            }
            catch (Exception e) when (e is IOException or System.Security.SecurityException)
            {
                _err.WriteLine($"   Unable to purge {entry.File.FullName} - {e.Message}");
                failed++;
            }
        }

        (Purged, Retained, Failed) = (purged, retained, failed);

        string summary = options.WhatIf
            ? $"Files that would be purged: {purged}   Files that would be retained: {retained}"
            : $"Files purged: {purged}   Files retained: {retained}" + (failed > 0 ? $"   Failed: {failed}" : "");
        if (folderErrors > 0)
            summary += $"   Folders skipped: {folderErrors}";
        _out.WriteLine($"{summary}   {stopwatch.Elapsed.TotalSeconds:f1} seconds");

        if (options.WhatIf)
            _out.WriteLine("NOTE: --whatif was set, no files were actually purged.");

        return failed > 0 || folderErrors > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    private char Ask()
    {
        _out.Write("   Purge this file? [Y]es / [N]o / [A]ll remaining / [Q]uit: ");
        char key;
        do
        {
            key = char.ToLowerInvariant(_readKey!());
        }
        while (key is not ('y' or 'n' or 'a' or 'q'));
        _out.WriteLine(key);
        return key;
    }
}
