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
    /// <param name="written">Receives the bytes overwritten so far in this file.</param>
    /// <param name="cancel">
    /// Stops the overwrite; <see cref="OperationCanceledException"/> is thrown (not returned)
    /// and the file is left in place, possibly partly overwritten.
    /// </param>
    public static DeleteResult Delete(string path, int passes, bool force,
                                      IProgress<long>? written = null, CancellationToken cancel = default)
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

            FileWiper.WipeAndDelete(file, passes, written, cancel);
            return new(path, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return new(path, Reason(e));
        }
    }

    /// <summary>Why a file was left when deleting was stopped while it was being overwritten.</summary>
    public const string StoppedWhileOverwriting =
        "stopped partway: partly overwritten, not deleted";

    /// <summary>Why a file was left when deleting was stopped before it was reached.</summary>
    public const string StoppedBefore = "not deleted: stopped before reaching it";

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDirNotEmpty = 145;

    /// <summary>
    /// A short, plain reason for a failed delete. Windows' own messages repeat the full path
    /// and are long, so the common cases are reworded.
    /// </summary>
    public static string Reason(Exception e) => e switch
    {
        UnauthorizedAccessException => "access denied",
        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation =>
            "in use by another program (close it and try again)",
        IOException io when (io.HResult & 0xFFFF) == ErrorDirNotEmpty =>
            "not empty: something was added to it",
        _ => e.Message,
    };
}
