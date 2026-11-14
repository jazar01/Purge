using System.Diagnostics;

namespace Purge.Tests;

public class RecurseTests
{
    private static int Run(PurgeOptions options, out string output, out string error)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = new PurgeProcessor(o, e).Run(options, TempDir.Now);
        output = o.ToString();
        error = e.ToString();
        return code;
    }

    [Fact]
    public void WithoutRecurse_SubfoldersAreUntouched()
    {
        using var dir = new TempDir();
        dir.File("top.zip", 40);
        dir.File("sub/inner.zip", 40);

        int code = Run(new PurgeOptions(dir.Spec("*.zip")), out _, out _);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["sub/inner.zip"], dir.AllNames());
    }

    [Fact]
    public void WithRecurse_PurgesMatchingFilesInAllSubfolders()
    {
        using var dir = new TempDir();
        dir.File("top.zip", 40);
        dir.File("a/one.zip", 40);
        dir.File("a/b/c/deep.zip", 40);
        dir.File("a/keep.txt", 40);
        dir.File("a/new.zip", 1);

        int code = Run(new PurgeOptions(dir.Spec("*.zip"), KeepDays: 30, Recurse: true), out string output, out _);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["a/keep.txt", "a/new.zip"], dir.AllNames());
        Assert.Contains("Files purged: 3   Files retained: 1", output);
    }

    [Fact]
    public void WithRecurse_KeepNumberAppliesToEachFolderSeparately()
    {
        using var dir = new TempDir();
        // A busy folder with many recent files must not cause the quiet folder's
        // last backups to be deleted.
        for (int i = 1; i <= 5; i++)
            dir.File($"busy/b{i}.zip", i);
        dir.File("quiet/q1.zip", 100);
        dir.File("quiet/q2.zip", 110);
        dir.File("quiet/q3.zip", 120);

        Run(new PurgeOptions(dir.Spec("*.zip"), KeepNumber: 2, KeepDays: 30, Recurse: true), out _, out _);

        Assert.Equal(
            ["busy/b1.zip", "busy/b2.zip", "busy/b3.zip", "busy/b4.zip", "busy/b5.zip", "quiet/q1.zip", "quiet/q2.zip"],
            dir.AllNames());
    }

    [Fact]
    public void WithRecurse_FoldersMatchingThePatternAreNotDeleted()
    {
        using var dir = new TempDir();
        dir.File("archive.zip/inside.txt", 40);

        int code = Run(new PurgeOptions(dir.Spec("*.zip"), Recurse: true), out string output, out _);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["archive.zip/inside.txt"], dir.AllNames());
        Assert.Contains("No files match", output);
    }

    [Fact]
    public void WithRecurse_FolderJunctionsAreNotFollowed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var outside = new TempDir();
        using var dir = new TempDir();
        dir.File("inside.zip", 40);
        outside.File("outside.zip", 40);

        string link = Path.Combine(dir.Path, "link");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        try
        {
            int code = Run(new PurgeOptions(dir.Spec("*.zip"), Recurse: true), out _, out _);

            Assert.Equal(ExitCodes.Success, code);
            Assert.Empty(dir.Names());
            Assert.Equal(["outside.zip"], outside.Names());
        }
        finally
        {
            // Remove just the link (non-recursive); a recursive delete of a folder holding
            // a junction can throw on some systems even though it removes the link.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void FolderListing_IncludesEveryFolderOnce_InNameOrder()
    {
        using var dir = new TempDir();
        dir.File("b/x.zip", 1);
        dir.File("a/x.zip", 1);
        dir.File("a/z/x.zip", 1);
        dir.File("x.zip", 1);

        var folders = FileList.FromFileSpec(dir.Spec("*.zip"), TempDir.Now, recurse: true);

        var order = folders.Select(f => Path.GetRelativePath(dir.Path, f.Items.Single().File.DirectoryName!)).ToArray();
        Assert.Equal([".", "a", Path.Combine("a", "z"), "b"], order);
    }
}
