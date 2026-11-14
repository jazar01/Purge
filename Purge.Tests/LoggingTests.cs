using System.Text.RegularExpressions;

namespace Purge.Tests;

public class LoggingTests
{
    private static readonly DateTime Clock = new(2026, 11, 14, 9, 30, 15);

    [Fact]
    public void TeeWriter_CopiesCompleteLinesWithTimestampAndTag()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "purge.log");
        var console = new StringWriter();

        using (var log = LogFile.Open(path, () => Clock))
        {
            using var output = new TeeWriter(console, log);
            using var error = new TeeWriter(console, log, "ERROR");
            output.WriteLine("Purging a.zip");
            output.Write("   Purge this file? [Y]es / [N]o: ");   // prompt and answer
            output.WriteLine('y');                                  // end up on one line
            error.WriteLine("   Unable to purge b.zip");
        }

        Assert.Equal(
            ["2026-11-14 09:30:15  Purging a.zip",
             "2026-11-14 09:30:15     Purge this file? [Y]es / [N]o: y",
             "2026-11-14 09:30:15  ERROR     Unable to purge b.zip"],
            File.ReadAllLines(path));
        Assert.Equal(
            "Purging a.zip" + Environment.NewLine +
            "   Purge this file? [Y]es / [N]o: y" + Environment.NewLine +
            "   Unable to purge b.zip" + Environment.NewLine,
            console.ToString());
    }

    [Fact]
    public void LogFile_AppendsAcrossRuns()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "purge.log");

        using (var log = LogFile.Open(path, () => Clock)) log.WriteLine("first");
        using (var log = LogFile.Open(path, () => Clock)) log.WriteLine("second");

        Assert.Equal(["2026-11-14 09:30:15  first", "2026-11-14 09:30:15  second"], File.ReadAllLines(path));
    }

    [Fact]
    public void LogFile_CreatesMissingFolder_AndRejectsAFolderPath()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "logs", "2026", "purge.log");

        using (LogFile.Open(path)) { }

        Assert.True(File.Exists(path));
        Assert.Throws<IOException>(() => LogFile.Open(dir.Path));
    }

    [Fact]
    public void ExcludedFile_IsNeverPurged_AndDoesNotCountTowardKeepNumber()
    {
        using var dir = new TempDir();
        string logPath = dir.File("purge.log", 100);
        dir.File("a.log", 40);
        dir.File("b.log", 50);
        dir.File("c.log", 60);

        new PurgeProcessor(TextWriter.Null, TextWriter.Null)
            .Run(new PurgeOptions(dir.Spec("*.log"), KeepNumber: 2, ExcludeFile: logPath), TempDir.Now);

        // purge.log is the oldest match but is kept; it is not one of the 2 kept files.
        Assert.Equal(["a.log", "b.log", "purge.log"], dir.Names());
    }
}

/// <summary>Runs the real command line; these tests swap Console.Out/Error, so they don't run in parallel.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection { }

[Collection("Console")]
public class LogOptionTests
{
    private static readonly Regex Stamp = new(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  ");

    private static int Invoke(params string[] args)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        try
        {
            return Program.BuildRootCommand().Parse(args).Invoke();
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void Log_RecordsTheRun_AndIsNotPurgedEvenWhenItMatches()
    {
        using var dir = new TempDir();
        dir.File("old.txt", 40);
        string logPath = dir.Spec("purge.txt");

        int code = Invoke("-d", "30", "--log", logPath, dir.Spec("*.txt"));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["purge.txt"], dir.Names());

        string[] lines = File.ReadAllLines(logPath);
        Assert.All(lines, l => Assert.Matches(Stamp, l));
        Assert.Contains("=== Purge 2.3.0 started: Purge -d 30 --log", lines[0]);
        Assert.Contains(lines, l => l.Contains("Purging (age 40 days)") && l.EndsWith("old.txt"));
        Assert.Contains(lines, l => l.Contains("Files purged: 1   Files retained: 0"));
        Assert.EndsWith("=== Finished, exit code 0", lines[^1]);
    }

    [Fact]
    public void Log_TagsErrors_AndRecordsTheExitCode()
    {
        using var dir = new TempDir();
        string logPath = dir.Spec("purge.log");

        int code = Invoke("--log", logPath, dir.Spec(@"missing\*.zip"));

        Assert.Equal(ExitCodes.Error, code);
        string[] lines = File.ReadAllLines(logPath);
        Assert.Contains(lines, l => l.Contains("  ERROR  Error: Directory not found"));
        Assert.EndsWith("=== Finished, exit code 1", lines[^1]);
    }

    [Fact]
    public void Log_ThatCannotBeOpened_StopsBeforeAnythingIsPurged()
    {
        using var dir = new TempDir();
        dir.File("old.zip", 40);

        int code = Invoke("--log", dir.Path, dir.Spec("*.zip"));   // a folder, not a file

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(["old.zip"], dir.Names());
    }
}
