namespace Purge.Tests;

public class FileListTests
{
    private static string[] Candidates(FileList list) =>
        list.Items.Where(f => f.IsCandidate).Select(f => f.File.Name).Order().ToArray();

    [Fact]
    public void KeepDays_MarksOnlyFilesOlderThanKeepDays()
    {
        using var dir = new TempDir();
        dir.File("a.zip", 1);
        dir.File("b.zip", 29.5);
        dir.File("c.zip", 30.5);
        dir.File("d.zip", 45);

        var list = FileList.FromFileSpec(dir.Spec("*.zip"), TempDir.Now);
        list.MarkFilesForDeletion(keepNumber: 0, keepDays: 30);

        Assert.Equal(["c.zip", "d.zip"], Candidates(list));
    }

    [Fact]
    public void KeepNumber_KeepsNewestFilesEvenWhenAllAreOld()
    {
        using var dir = new TempDir();
        for (int i = 1; i <= 6; i++)
            dir.File($"backup{i}.zip", 40 + i);   // backup1 is newest

        var list = FileList.FromFileSpec(dir.Spec("*.zip"), TempDir.Now);
        list.MarkFilesForDeletion(keepNumber: 5, keepDays: 30);

        Assert.Equal(["backup6.zip"], Candidates(list));
        Assert.Equal(5, list.KeepCount);
    }

    [Fact]
    public void KeepNumber_UsesExactTimestamps_ForFilesOfTheSameDay()
    {
        // Several backups on the same (old) day: the original code sorted by whole days,
        // so which ones survived --keep-number was arbitrary.
        using var dir = new TempDir();
        dir.File("0600.zip", 40.75);
        dir.File("1200.zip", 40.50);
        dir.File("1800.zip", 40.25);
        dir.File("0000.zip", 40.99);

        var list = FileList.FromFileSpec(dir.Spec("*.zip"), TempDir.Now);
        list.MarkFilesForDeletion(keepNumber: 2, keepDays: 30);

        Assert.Equal(["0000.zip", "0600.zip"], Candidates(list));
    }

    [Fact]
    public void KeepNumber_CountsRecentFilesTowardTheMinimum()
    {
        using var dir = new TempDir();
        dir.File("new1.zip", 1);
        dir.File("new2.zip", 2);
        dir.File("old1.zip", 50);
        dir.File("old2.zip", 60);

        var list = FileList.FromFileSpec(dir.Spec("*.zip"), TempDir.Now);
        list.MarkFilesForDeletion(keepNumber: 3, keepDays: 30);

        Assert.Equal(["old2.zip"], Candidates(list));
    }

    [Fact]
    public void Defaults_MarkEverything()
    {
        using var dir = new TempDir();
        dir.File("a.log", 0.01);
        dir.File("b.log", 5);

        var list = FileList.FromFileSpec(dir.Spec("*.log"), TempDir.Now);
        list.MarkFilesForDeletion(0, 0);

        Assert.Equal(2, list.PurgeCount);
    }

    [Fact]
    public void ThreeLetterExtension_DoesNotMatchLongerExtensions()
    {
        using var dir = new TempDir();
        dir.File("a.bak", 1);
        dir.File("b.bakup", 1);
        dir.File("c.BAK", 1);

        var list = FileList.FromFileSpec(dir.Spec("*.bak"), TempDir.Now);

        Assert.Equal(["a.bak", "c.BAK"], list.Items.Select(f => f.File.Name).Order().ToArray());
    }

    [Fact]
    public void DirectoryWithoutPattern_IsRejected()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => FileList.FromFileSpec(dir.Path, TempDir.Now));
        Assert.Throws<ArgumentException>(() => FileList.FromFileSpec(dir.Path + System.IO.Path.DirectorySeparatorChar, TempDir.Now));
    }

    [Fact]
    public void WildcardInDirectory_IsRejected()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => FileList.FromFileSpec(System.IO.Path.Combine(dir.Path, "sub*", "*.zip"), TempDir.Now));
    }

    [Fact]
    public void MissingDirectory_Throws()
    {
        using var dir = new TempDir();
        Assert.Throws<DirectoryNotFoundException>(() => FileList.FromFileSpec(dir.Spec(@"nope\*.zip"), TempDir.Now));
    }
}
