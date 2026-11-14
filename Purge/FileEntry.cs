namespace Purge;

/// <summary>
/// A file matched by the file spec, with its age and whether it is marked for purging.
/// </summary>
public sealed class FileEntry
{
    public FileEntry(FileInfo file, DateTime nowUtc)
    {
        File = file;
        LastWriteUtc = file.LastWriteTimeUtc;
        AgeDays = (nowUtc - LastWriteUtc).TotalDays;
    }

    public FileInfo File { get; }

    /// <summary>Exact last-modified time; used for ordering.</summary>
    public DateTime LastWriteUtc { get; }

    /// <summary>Exact age in (fractional) days since last modified.</summary>
    public double AgeDays { get; }

    /// <summary>Age in whole days, for display.</summary>
    public int Age => (int)Math.Floor(AgeDays);

    public bool IsCandidate { get; set; }
}
