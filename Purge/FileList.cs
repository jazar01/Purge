namespace Purge;

/// <summary>
/// The files matching a file spec, ordered newest first, and the logic that decides
/// which of them are purged.
/// </summary>
public sealed class FileList
{
    private static readonly char[] Wildcards = ['*', '?'];

    public FileList(IEnumerable<FileEntry> items)
    {
        // Order by exact timestamp (not whole days) so --keep-number always keeps the
        // truly newest files, with the path as a stable tie-breaker.
        Items = items
            .OrderByDescending(f => f.LastWriteUtc)
            .ThenBy(f => f.File.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Files, newest first.</summary>
    public IReadOnlyList<FileEntry> Items { get; }

    public int KeepCount => Items.Count(f => !f.IsCandidate);
    public int PurgeCount => Items.Count(f => f.IsCandidate);

    /// <summary>
    /// Builds the list from a file spec such as <c>D:\Backups\Finance*.zip</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The spec is empty, is a directory, or has wildcards in the directory part.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static FileList FromFileSpec(string fileSpec, DateTime nowUtc)
    {
        var (directory, pattern) = SplitFileSpec(fileSpec);
        return FromDirectory(new DirectoryInfo(directory), pattern, nowUtc);
    }

    /// <summary>
    /// Builds one list per folder: the spec's folder and, with <paramref name="recurse"/>,
    /// every subfolder below it. Each folder is its own set for keep-number/keep-days, so
    /// files in one folder never count toward (or against) another folder's minimum.
    /// </summary>
    /// <remarks>
    /// Folder junctions and symbolic links are not followed, so the walk cannot loop or
    /// leave the tree. Errors reading the top folder are thrown; errors reading a
    /// subfolder are passed to <paramref name="onFolderError"/> and that folder is skipped.
    /// </remarks>
    public static IReadOnlyList<FileList> FromFileSpec(string fileSpec, DateTime nowUtc, bool recurse,
                                                       Action<DirectoryInfo, Exception>? onFolderError = null)
    {
        var (directory, pattern) = SplitFileSpec(fileSpec);
        var top = new DirectoryInfo(directory);
        var lists = new List<FileList> { FromDirectory(top, pattern, nowUtc) };

        if (!recurse)
            return lists;

        var pending = new Stack<DirectoryInfo>(SubfoldersOf(top).Reverse());
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            try
            {
                lists.Add(FromDirectory(folder, pattern, nowUtc));
                foreach (var sub in SubfoldersOf(folder).Reverse())
                    pending.Push(sub);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (onFolderError is null) throw;
                onFolderError(folder, e);
            }
        }

        return lists;
    }

    private static readonly EnumerationOptions FolderOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };

    /// <summary>Immediate subfolders, sorted by name, excluding junctions and symbolic links.</summary>
    private static IEnumerable<DirectoryInfo> SubfoldersOf(DirectoryInfo folder) =>
        folder.EnumerateDirectories("*", FolderOptions)
              .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
              .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();

    private static FileList FromDirectory(DirectoryInfo folder, string pattern, DateTime nowUtc)
    {
        var options = new EnumerationOptions
        {
            // Simple matching: only * and ?, and no legacy DOS quirks such as
            // "*.bak" also matching "*.bakup".
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0, // include hidden/system files, as the original version did
        };

        // ToList so enumeration errors surface here, not later while purging.
        var files = folder
            .EnumerateFiles(pattern, options)
            .Select(fi => new FileEntry(fi, nowUtc))
            .ToList();

        return new FileList(files);
    }

    public static (string Directory, string Pattern) SplitFileSpec(string fileSpec)
    {
        if (string.IsNullOrWhiteSpace(fileSpec))
            throw new ArgumentException("FileSpec must not be empty.");

        if (Directory.Exists(fileSpec))
            throw new ArgumentException(
                $"'{fileSpec}' is a directory. Add a file pattern, e.g. {Path.Combine(fileSpec, "*.zip")}");

        string? directory = Path.GetDirectoryName(fileSpec);
        string pattern = Path.GetFileName(fileSpec);

        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException($"'{fileSpec}' has no file pattern, e.g. {Path.Combine(fileSpec, "*.zip")}");

        if (string.IsNullOrEmpty(directory))
            directory = Environment.CurrentDirectory;
        else if (directory.IndexOfAny(Wildcards) >= 0)
            throw new ArgumentException("Wildcards are allowed in the file name but not in the directory part of FileSpec.");

        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory not found: {directory}");

        return (directory, pattern);
    }

    /// <summary>
    /// Marks files older than <paramref name="keepDays"/> days for purging, but never
    /// so many that fewer than <paramref name="keepNumber"/> files would remain.
    /// The newest files are the ones kept.
    /// </summary>
    public void MarkFilesForDeletion(int keepNumber, int keepDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(keepDays);

        int kept = 0;
        foreach (var file in Items) // newest first
        {
            bool tooOld = file.AgeDays > keepDays;
            if (!tooOld || kept < keepNumber)
            {
                file.IsCandidate = false;
                kept++;
            }
            else
            {
                file.IsCandidate = true;
            }
        }
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in Items)
            sb.AppendLine($"{file.Age}\t{file.IsCandidate}\t{file.File.FullName}");
        sb.AppendLine($"Total count: {Items.Count}  To be deleted: {PurgeCount}  To be kept: {KeepCount}");
        return sb.ToString();
    }
}
