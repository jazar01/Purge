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
                    unreadable.Add(new(dir.FullName, e is UnauthorizedAccessException ? "access denied" : e.Message));
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
                failures.Add(new(link, e is UnauthorizedAccessException ? "access denied" : e.Message));
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
                failures.Add(new(folder, e is UnauthorizedAccessException ? "access denied" : e.Message));
            }
        }

        bool removed = !Directory.Exists(contents.Path) && !System.IO.File.Exists(contents.Path);
        if (!removed && !failures.Any(f => string.Equals(f.Path, contents.Path, StringComparison.OrdinalIgnoreCase)))
            failures.Add(new(contents.Path, "kept because some of its contents could not be deleted"));

        return new FolderDeleteResult(deleted, failures, removed);
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

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
}
