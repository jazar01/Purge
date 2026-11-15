using System.Diagnostics;

namespace Purge.Tests;

public class WipeHardeningTests
{
    private static bool Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Makes an NTFS-compressed file, or returns null if the drive can't compress.</summary>
    private static string? CompressedFile(TempDir dir)
    {
        string path = dir.File("packed.txt", 40, new string('a', 10_000));
        Run("compact.exe", $"/c /q \"{path}\"");
        return File.GetAttributes(path).HasFlag(FileAttributes.Compressed) ? path : null;
    }

    [Fact]
    public void AlternateDataStreams_AreOverwrittenAndDeleted()
    {
        using var dir = new TempDir();
        string path = dir.File("report.docx", 1, "main content");
        File.WriteAllText(path + ":hidden", "secret stream content");

        Assert.Equal([":hidden:$DATA"], FileWiper.AlternateStreams(path));

        FileWiper.Overwrite(new FileInfo(path), passes: 1);
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Equal("", File.ReadAllText(path + ":hidden"));   // stream overwritten and emptied

        FileWiper.WipeAndDelete(new FileInfo(path), passes: 1);
        Assert.Empty(dir.Names());
    }

    [Fact]
    public void FileWithoutStreams_HasNone()
    {
        using var dir = new TempDir();
        Assert.Empty(FileWiper.AlternateStreams(dir.File("plain.txt", 1)));
    }

    [Fact]
    public void CompressedFile_IsWarnedAbout()
    {
        using var dir = new TempDir();
        if (CompressedFile(dir) is not { } path)
            return; // drive doesn't support NTFS compression

        Assert.Contains(WipeCheck.For(new FileInfo(path)), w => w.Title == "Compressed file" && !w.AppliesToDrive);
    }

    [Fact]
    public void SparseFile_IsWarnedAbout()
    {
        using var dir = new TempDir();
        string path = dir.File("sparse.bin", 40);
        if (!Run("fsutil.exe", $"sparse setflag \"{path}\""))
            return; // fsutil unavailable or not permitted

        Assert.Contains(WipeCheck.For(new FileInfo(path)), w => w.Title == "Sparse file");
    }

    [Fact]
    public void OrdinaryFile_HasNoFileWarnings()
    {
        using var dir = new TempDir();
        Assert.DoesNotContain(WipeCheck.For(new FileInfo(dir.File("plain.txt", 1))), w => !w.AppliesToDrive);
    }

    [Fact]
    public void OneDriveFolder_IsWarnedAbout()
    {
        using var dir = new TempDir();
        string path = dir.File("doc.txt", 1);
        string? saved = Environment.GetEnvironmentVariable("OneDriveConsumer");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveConsumer", dir.Path);
            Assert.Contains(WipeCheck.For(new FileInfo(path)), w => w.Title == "OneDrive folder" && w.AppliesToDrive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveConsumer", saved);
        }
    }

    [Fact]
    public void SolidStateQuery_AnswersForSystemDrive()
    {
        // Any answer (true, false, or "unknown") is fine; it just mustn't fail.
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        _ = WipeCheck.IsSolidState(drive);
        Assert.Null(WipeCheck.IsSolidState("Q:")); // no such drive
    }

    [Fact]
    public void Cli_PrintsNotes_OnlyWhenOverwriting()
    {
        using var dir = new TempDir();
        if (CompressedFile(dir) is null)
            return;

        string Output(int passes)
        {
            var output = new StringWriter();
            new PurgeProcessor(output, TextWriter.Null)
                .Run(new PurgeOptions(dir.Spec("*.txt"), SecurityLevel: passes, WhatIf: true), TempDir.Now);
            return output.ToString();
        }

        Assert.Contains("   Note: Compressed file - NTFS may write", Output(passes: 1));
        Assert.DoesNotContain("Note:", Output(passes: 0));
    }

    [Fact]
    public void Cli_DriveNotes_AreShownOnce()
    {
        using var dir = new TempDir();
        dir.File("a.txt", 40);
        dir.File("b.txt", 50);
        string? saved = Environment.GetEnvironmentVariable("OneDriveConsumer");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveConsumer", dir.Path);
            var output = new StringWriter();
            new PurgeProcessor(output, TextWriter.Null)
                .Run(new PurgeOptions(dir.Spec("*.txt"), SecurityLevel: 1, WhatIf: true), TempDir.Now);

            Assert.Single(output.ToString().Split('\n'), line => line.Contains("Note: OneDrive folder"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveConsumer", saved);
        }
    }
}
