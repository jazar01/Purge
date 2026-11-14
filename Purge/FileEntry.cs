namespace Purge;

/// <summary>
/// A file matched by the file spec, with its age and whether it is marked for purging.
/// </summary>
public sealed class FileEntry
{
    public FileEntry(FileInfo file, DateTime nowUtc, AgeBy ageBy = AgeBy.Modified)
    {
        File = file;
        TimestampUtc = ageBy == AgeBy.Created ? file.CreationTimeUtc : file.LastWriteTimeUtc;
        AgeDays = (nowUtc - TimestampUtc).TotalDays;
    }

    public FileInfo File { get; }

    /// <summary>The exact time the age is measured from (modified or created); used for ordering.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Exact age in (fractional) days.</summary>
    public double AgeDays { get; }

    /// <summary>Age in whole days, for display.</summary>
    public int Age => (int)Math.Floor(AgeDays);

    public bool IsCandidate { get; set; }
}
