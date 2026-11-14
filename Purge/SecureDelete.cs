namespace Purge;

/// <summary>The outcome of deleting one hand-picked file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Error">Why it could not be deleted, or null if it was deleted.</param>
public sealed record DeleteResult(string Path, string? Error)
{
    public bool Deleted => Error is null;
}

/// <summary>
/// Securely deletes individual files chosen by the user (rather than by a pattern and
/// keep rules). Used by the Explorer "Securely delete" command.
/// </summary>
public static class SecureDelete
{
    /// <summary>
    /// Overwrites the file <paramref name="passes"/> times and deletes it. Never throws for
    /// ordinary file problems; they are returned in <see cref="DeleteResult.Error"/>.
    /// </summary>
    /// <param name="force">Also delete read-only files.</param>
    public static DeleteResult Delete(string path, int passes, bool force)
    {
        try
        {
            if (Directory.Exists(path))
                return new(path, "is a folder; only files are deleted");

            var file = new FileInfo(path);
            if (!file.Exists)
                return new(path, "file not found");

            if (file.IsReadOnly)
            {
                if (!force)
                    return new(path, "read-only");
                file.IsReadOnly = false;
            }

            FileWiper.WipeAndDelete(file, passes);
            return new(path, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(path, "access denied");
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return new(path, e.Message);
        }
    }
}
