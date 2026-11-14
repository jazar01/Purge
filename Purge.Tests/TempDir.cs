namespace Purge.Tests;

/// <summary>A throwaway directory for a test, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

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
        System.IO.File.WriteAllText(full, content);
        System.IO.File.SetLastWriteTimeUtc(full, Now.AddDays(-ageDays));
        return full;
    }

    public string[] Names() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order().ToArray()!;

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var f in Directory.GetFiles(Path))
            System.IO.File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
