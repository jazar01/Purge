namespace Purge.Tests;

/// <summary>A throwaway directory for a test, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    /// <summary>
    /// The "current time" file ages are measured from: the real time when the tests start.
    /// It has to be the real time because the command-line tests run Purge, which reads
    /// the clock itself; a fixed date made those tests fail once that date had passed.
    /// </summary>
    public static readonly DateTime Now = DateTime.UtcNow;

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PurgeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Spec(string pattern) => System.IO.Path.Combine(Path, pattern);

    /// <summary>Creates a file whose last-write time is <paramref name="ageDays"/> before <see cref="Now"/>.</summary>
    public string File(string name, double ageDays, string content = "data")
    {
        string full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        System.IO.File.SetLastWriteTimeUtc(full, Now.AddDays(-ageDays));
        return full;
    }

    public string[] Names() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order().ToArray()!;

    /// <summary>All files below the directory, as relative paths with '/' separators.</summary>
    public string[] AllNames() =>
        Directory.GetFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                 .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'))
                 .Order(StringComparer.Ordinal)
                 .ToArray();

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var f in Directory.GetFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            System.IO.File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
