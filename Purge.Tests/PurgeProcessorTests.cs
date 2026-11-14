namespace Purge.Tests;

public class PurgeProcessorTests
{
    private static (int Code, string Out, string Err) Run(PurgeOptions options, Func<char>? readKey = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = new PurgeProcessor(output, error, readKey).Run(options, TempDir.Now);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Purges_OldFiles_AndKeepsTheRest()
    {
        using var dir = new TempDir();
        dir.File("new.zip", 1);
        dir.File("old.zip", 40);
        dir.File("other.txt", 40);

        var (code, output, _) = Run(new PurgeOptions(dir.Spec("*.zip"), KeepDays: 30));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["new.zip", "other.txt"], dir.Names());
        Assert.Contains("Files purged: 1   Files retained: 1", output);
    }

    [Fact]
    public void WhatIf_DeletesNothing_AndReportsWhatWouldHappen()
    {
        using var dir = new TempDir();
        dir.File("a.zip", 40);
        dir.File("b.zip", 50);

        var (code, output, _) = Run(new PurgeOptions(dir.Spec("*.zip"), WhatIf: true));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(2, dir.Names().Length);
        Assert.Contains("Files that would be purged: 2", output);
    }

    [Fact]
    public void ReadOnlyFile_WithoutForce_IsReportedAsFailure()
    {
        using var dir = new TempDir();
        string ro = dir.File("ro.zip", 40);
        dir.File("rw.zip", 40);
        System.IO.File.SetAttributes(ro, FileAttributes.ReadOnly);

        var (code, output, error) = Run(new PurgeOptions(dir.Spec("*.zip")));

        Assert.Equal(ExitCodes.PartialFailure, code);
        Assert.Equal(["ro.zip"], dir.Names());
        Assert.Contains("read-only (use --force)", error);
        Assert.Contains("Failed: 1", output);
    }

    [Fact]
    public void ReadOnlyFile_WithForce_IsPurged()
    {
        using var dir = new TempDir();
        string ro = dir.File("ro.zip", 40);
        System.IO.File.SetAttributes(ro, FileAttributes.ReadOnly);

        var (code, _, _) = Run(new PurgeOptions(dir.Spec("*.zip"), Force: true, SecurityLevel: 2));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(dir.Names());
    }

    [Fact]
    public void Prompt_WithoutInteractiveConsole_FailsWithoutDeleting()
    {
        using var dir = new TempDir();
        dir.File("a.zip", 40);

        var (code, _, error) = Run(new PurgeOptions(dir.Spec("*.zip"), Prompt: true), readKey: null);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Single(dir.Names());
        Assert.Contains("--prompt", error);
    }

    [Fact]
    public void Prompt_AnswersAreHonoured()
    {
        using var dir = new TempDir();
        dir.File("a.zip", 40);   // newest candidate, asked first
        dir.File("b.zip", 50);
        dir.File("c.zip", 60);

        var keys = new Queue<char>(['x', 'N', 'y', 'q']);   // 'x' is ignored
        var (code, output, _) = Run(new PurgeOptions(dir.Spec("*.zip"), Prompt: true), keys.Dequeue);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["a.zip", "c.zip"], dir.Names());
        Assert.Contains("Files purged: 1   Files retained: 2", output);
    }

    [Fact]
    public void Prompt_All_PurgesTheRestWithoutAsking()
    {
        using var dir = new TempDir();
        dir.File("a.zip", 40);
        dir.File("b.zip", 50);

        var keys = new Queue<char>(['a']);
        var (code, _, _) = Run(new PurgeOptions(dir.Spec("*.zip"), Prompt: true), keys.Dequeue);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(dir.Names());
    }

    [Fact]
    public void MissingDirectory_ReturnsError()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run(new PurgeOptions(dir.Spec(@"missing\*.zip")));

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("Directory not found", error);
    }

    [Fact]
    public void NoMatches_IsSuccess()
    {
        using var dir = new TempDir();
        var (code, output, _) = Run(new PurgeOptions(dir.Spec("*.zip")));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("No files match", output);
    }
}
