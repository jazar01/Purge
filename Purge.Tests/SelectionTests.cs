namespace Purge.Tests;

public class SelectionTests
{
    private static string Sized(TempDir dir, string name, long bytes, double ageDays)
    {
        string path = dir.File(name, ageDays, content: "");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            fs.SetLength(bytes);
        File.SetLastWriteTimeUtc(path, TempDir.Now.AddDays(-ageDays));
        return path;
    }

    private static int Run(PurgeOptions options) =>
        new PurgeProcessor(TextWriter.Null, TextWriter.Null).Run(options, TempDir.Now);

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("500", 500L)]
    [InlineData("1b", 1L)]
    [InlineData("10KB", 10L * 1024)]
    [InlineData("10k", 10L * 1024)]
    [InlineData("1.5MB", 1536L * 1024)]
    [InlineData(" 2 GB ", 2L * 1024 * 1024 * 1024)]
    [InlineData("1TB", 1024L * 1024 * 1024 * 1024)]
    public void Size_Parses(string text, long expected)
    {
        Assert.True(Size.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("10XB")]
    [InlineData("1,5MB")]
    [InlineData("KB")]
    [InlineData("10 MBB")]
    [InlineData("99999999999TB")]
    public void Size_RejectsInvalid(string text) => Assert.False(Size.TryParse(text, out _));

    [Fact]
    public void SizeLimits_OnlyAffectFilesInRange()
    {
        using var dir = new TempDir();
        Sized(dir, "tiny.bak", 100, 40);
        Sized(dir, "mid.bak", 2 * 1024, 40);
        Sized(dir, "edge.bak", 10 * 1024, 40);   // limits are inclusive
        Sized(dir, "big.bak", 20 * 1024, 40);

        Run(new PurgeOptions(dir.Spec("*.bak"), Selection: new FileSelection(MinSize: 1024, MaxSize: 10 * 1024)));

        Assert.Equal(["big.bak", "tiny.bak"], dir.Names());
    }

    [Fact]
    public void SizeLimits_FilesOutsideRange_DoNotCountTowardKeepNumber()
    {
        using var dir = new TempDir();
        // Tiny, broken backups are newer than the real ones.
        Sized(dir, "broken1.bak", 10, 1);
        Sized(dir, "broken2.bak", 10, 2);
        Sized(dir, "full1.bak", 4096, 40);
        Sized(dir, "full2.bak", 4096, 50);

        // Keep at least 1 real (>= 1KB) backup; the tiny files are invisible to the rule.
        Run(new PurgeOptions(dir.Spec("*.bak"), KeepNumber: 1, KeepDays: 30,
                             Selection: new FileSelection(MinSize: 1024)));

        Assert.Equal(["broken1.bak", "broken2.bak", "full1.bak"], dir.Names());
    }

    [Fact]
    public void AgeByCreated_UsesCreationTime()
    {
        using var dir = new TempDir();
        string path = dir.File("copied.zip", ageDays: 1);                // modified yesterday
        File.SetCreationTimeUtc(path, TempDir.Now.AddDays(-90));        // created 90 days ago

        Run(new PurgeOptions(dir.Spec("*.zip"), KeepDays: 30));
        Assert.Single(dir.Names());                                     // by modified: kept

        Run(new PurgeOptions(dir.Spec("*.zip"), KeepDays: 30, Selection: new FileSelection(AgeBy: AgeBy.Created)));
        Assert.Empty(dir.Names());                                      // by created: purged
    }

    [Fact]
    public void AgeByCreated_DecidesWhichFilesAreNewestForKeepNumber()
    {
        using var dir = new TempDir();
        string a = dir.File("a.zip", ageDays: 50);   // modified long ago, created recently
        string b = dir.File("b.zip", ageDays: 40);   // modified more recently, created long ago
        File.SetCreationTimeUtc(a, TempDir.Now.AddDays(-35));
        File.SetCreationTimeUtc(b, TempDir.Now.AddDays(-60));

        Run(new PurgeOptions(dir.Spec("*.zip"), KeepNumber: 1, KeepDays: 30,
                             Selection: new FileSelection(AgeBy: AgeBy.Created)));

        Assert.Equal(["a.zip"], dir.Names());
    }

    [Theory]
    [InlineData("--age-by", "created")]
    [InlineData("--age-by", "CREATED")]
    [InlineData("--age-by", "Modified")]
    [InlineData("--min-size", "10KB")]
    [InlineData("--max-size", "2.5GB")]
    public void NewOptions_Parse(string option, string value) =>
        Assert.Empty(Program.BuildRootCommand().Parse([option, value, "*.zip"]).Errors);

    [Theory]
    [InlineData("--age-by", "accessed")]
    [InlineData("--min-size", "10XB")]
    [InlineData("--max-size", "-5")]
    public void NewOptions_RejectBadValues(string option, string value) =>
        Assert.NotEmpty(Program.BuildRootCommand().Parse([option, value, "*.zip"]).Errors);

    [Fact]
    public void MinSizeLargerThanMaxSize_IsRejected() =>
        Assert.NotEmpty(Program.BuildRootCommand().Parse(["--min-size", "2MB", "--max-size", "1MB", "*.zip"]).Errors);
}
