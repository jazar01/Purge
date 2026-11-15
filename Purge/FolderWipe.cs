namespace Purge;

/// <summary>Everything inside a folder that a secure delete will remove.</summary>
public sealed class FolderContents
{
    /// <summary>The folder itself.</summary>
    public required string Path { get; init; }

    /// <summary>All files in the folder and its subfolders.</summary>
    public required IReadOnlyList<FileInfo> Files { get; init; }

    /// <summary>The folder and its subfolders, deepest first, the folder itself last.</summary>
    public required IReadOnlyList<string> Folders { get; init; }

    /// <summary>
    /// Folder junctions and symbolic links. Only the link is removed, never what it points to.
    /// </summary>
    public required IReadOnlyList<string> Links { get; init; }

    /// <summary>Subfolders that could not be read; they (and so their parents) will be left.</summary>
    public required IReadOnlyList<DeleteResult> Unreadable { get; init; }

    public long TotalSize => Files.Sum(f => f.Length);

    /// <summary>Subfolders, not counting the folder itself.</summary>
    public int SubfolderCount => Math.Max(0, Folders.Count - 1);
}

/// <summary>The outcome of securely deleting a folder.</summary>
/// <param name="FilesDeleted">Files overwritten and deleted.</param>
/// <param name="Failures">Files, links and folders that could not be removed, and why.</param>
/// <param name="FolderRemoved">True if the folder itself is gone.</param>
public sealed record FolderDeleteResult(int FilesDeleted, IReadOnlyList<DeleteResult> Failures, bool FolderRemoved);

/// <summary>
/// Securely deletes a whole folder: every file in it and its subfolders is overwritten
/// and deleted, then the emptied folders are removed, including the folder itself.
/// </summary>
/// <remarks>
/// Folder junctions and symbolic links inside the folder are removed as links; what they
/// point to is never touched. If anything can't be deleted (a file in use, an unreadable
/// subfolder), the folders still holding it are left in place and the rest is deleted.
/// </remarks>
public static class FolderWipe
{
    private static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Why this folder must not be securely deleted (a drive root, or a folder that is or
    /// contains a Windows system folder or the user profile), or null if it may.
    /// </summary>
    public static string? WhyNotAllowed(string folder)
    {
        string full;
        try
        {
            full = Trim(System.IO.Path.GetFullPath(folder));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "not a valid folder path";
        }

        string? root = System.IO.Path.GetPathRoot(full);
        if (root is null || string.Equals(full, Trim(root), StringComparison.OrdinalIgnoreCase))
            return "a whole drive can't be deleted";

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] protectedFolders =
        [
            windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            profile,
            System.IO.Path.GetDirectoryName(profile) ?? "", // C:\Users
        ];

        foreach (string p in protectedFolders.Where(p => p.Length > 0).Select(Trim))
        {
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase) || IsInside(p, full))
                return $"it is or contains a protected system folder ({p})";
        }
        if (windows.Length > 0 && IsInside(full, Trim(windows)))
            return "it is inside the Windows folder";

        return null;
    }

    /// <summary>Lists everything in the folder, without following links.</summary>
    public static FolderContents Scan(string folder)
    {
        var root = new DirectoryInfo(folder);
        var files = new List<FileInfo>();
        var links = new List<string>();
        var unreadable = new List<DeleteResult>();
        var preOrder = new List<string>();

        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // The selected folder is itself a junction or link: remove just the link.
            links.Add(root.FullName);
        }
        else
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                preOrder.Add(dir.FullName);
                try
                {
                    foreach (var entry in dir.EnumerateFileSystemInfos("*", Everything).ToList())
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            links.Add(entry.FullName);
                        else if (entry is DirectoryInfo sub)
                            pending.Push(sub);
                        else
                            files.Add((FileInfo)entry);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    unreadable.Add(new(dir.FullName, SecureDelete.Reason(e)));
                }
            }
        }

        // Reversed pre-order puts every folder after all of its subfolders.
        preOrder.Reverse();
        return new FolderContents
        {
            Path = root.FullName,
            Files = files,
            Folders = preOrder,
            Links = links,
            Unreadable = unreadable,
        };
    }

    /// <summary>
    /// Overwrites and deletes every file, removes links, then removes the emptied folders.
    /// </summary>
    /// <param name="progress">Receives the number of files processed so far.</param>
    public static FolderDeleteResult Delete(FolderContents contents, int passes, bool force,
                                            IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        var failures = new List<DeleteResult>(contents.Unreadable);
        int deleted = 0, done = 0;

        foreach (var file in contents.Files)
        {
            cancel.ThrowIfCancellationRequested();
            var result = SecureDelete.Delete(file.FullName, passes, force);
            if (result.Deleted)
                deleted++;
            else
                failures.Add(result);
            progress?.Report(++done);
        }

        foreach (string link in contents.Links)
        {
            try
            {
                // Non-recursive: removes the junction or link itself, not its target's contents.
                if (Directory.Exists(link))
                    Directory.Delete(link, recursive: false);
                else
                    System.IO.File.Delete(link);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add(new(link, SecureDelete.Reason(e)));
            }
        }

        foreach (string folder in contents.Folders) // deepest first
        {
            if (!Directory.Exists(folder))
                continue;
            if (Directory.EnumerateFileSystemEntries(folder).Any())
                continue; // still holds something that couldn't be deleted (already reported)

            try
            {
                RemoveEmptyFolder(folder, obscureName: passes > 0);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add(new(folder, SecureDelete.Reason(e)));
            }
        }

        bool removed = !Directory.Exists(contents.Path) && !System.IO.File.Exists(contents.Path);
        if (!removed)
        {
            ReportLeftovers(contents.Path, failures);
            if (!failures.Any(f => SamePath(f.Path, contents.Path)))
                failures.Add(new(contents.Path, KeptMessage));
        }

        return new FolderDeleteResult(deleted, failures, removed);
    }

    /// <summary>The reason given for a folder kept only because something inside it is left.</summary>
    public const string KeptMessage = "kept because it still contains the items listed";

    /// <summary>
    /// Adds anything still in the folder that isn't explained yet, such as files that
    /// appeared after the folder was scanned, so every item left behind has a reason.
    /// </summary>
    private static void ReportLeftovers(string folder, List<DeleteResult> failures)
    {
        FolderContents now;
        try
        {
            now = Scan(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        bool Explained(string path) => failures.Any(f =>
            SamePath(f.Path, path) ||
            (!SamePath(f.Path, folder) && // inside a reported subfolder (e.g. an unreadable one)
             path.StartsWith(Trim(f.Path) + "\\", StringComparison.OrdinalIgnoreCase)));

        foreach (var file in now.Files.Where(f => !Explained(f.FullName)))
            failures.Add(new(file.FullName, "added after the folder was scanned; not deleted"));
        foreach (string link in now.Links.Where(l => !Explained(l)))
            failures.Add(new(link, "added after the folder was scanned; not deleted"));
        foreach (var unreadable in now.Unreadable.Where(u => !Explained(u.Path)))
            failures.Add(unreadable);
    }

    /// <summary>Removes an empty folder, clearing its read-only flag and optionally renaming it first.</summary>
    internal static void RemoveEmptyFolder(string folder, bool obscureName)
    {
        // A folder's read-only flag is mostly used by Explorer for customised folders and
        // doesn't protect it, but it does block deleting, so clear it.
        var dir = new DirectoryInfo(folder);
        if (dir.Attributes.HasFlag(FileAttributes.ReadOnly))
            dir.Attributes &= ~FileAttributes.ReadOnly;

        if (obscureName && dir.Parent is { } parent)
        {
            string renamed = System.IO.Path.Combine(parent.FullName, System.IO.Path.GetRandomFileName());
            try
            {
                Directory.Move(folder, renamed);
                folder = renamed;
            }
            catch (IOException)
            {
                // Renaming is a nicety; still delete under the original name.
            }
        }

        Directory.Delete(folder, recursive: false);
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private static bool SamePath(string a, string b) => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
}
