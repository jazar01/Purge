using System.Diagnostics;
using PurgeWindow;

namespace Purge.Tests;

public class FolderWipeTests
{
    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    /// <summary>A folder "Secrets" inside the temp dir, with files in nested subfolders.</summary>
    private static string MakeTree(TempDir dir)
    {
        dir.File(@"Secrets\top.txt", 1, "top");
        dir.File(@"Secrets\Sub\middle.txt", 1, "middle");
        dir.File(@"Secrets\Sub\Deeper\bottom.txt", 1, "bottom");
        Directory.CreateDirectory(Path.Combine(dir.Path, @"Secrets\Empty"));
        return Path.Combine(dir.Path, "Secrets");
    }

    [Fact]
    public void Scan_FindsEverything_DeepestFoldersFirst()
    {
        using var dir = new TempDir();
        string folder = MakeTree(dir);

        var contents = FolderWipe.Scan(folder);

        Assert.Equal(3, contents.Files.Count);
        Assert.Equal(3, contents.SubfolderCount);
        Assert.Equal(folder, contents.Folders[^1]); // the folder itself is removed last
        int deeper = contents.Folders.ToList().IndexOf(Path.Combine(folder, @"Sub\Deeper"));
        int sub = contents.Folders.ToList().IndexOf(Path.Combine(folder, "Sub"));
        Assert.True(deeper < sub);
    }

    [Fact]
    public void Delete_RemovesFilesSubfoldersAndTheFolder()
    {
        using var dir = new TempDir();
        string folder = MakeTree(dir);
        dir.File("keep-me.txt", 1); // next to the folder, not in it

        var result = FolderWipe.Delete(FolderWipe.Scan(folder), passes: 1, force: false);

        Assert.True(result.FolderRemoved);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.FilesDeleted);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(["keep-me.txt"], dir.AllNames());
    }

    [Fact]
    public void Delete_JunctionInside_RemovesOnlyTheLink()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        outside.File("precious.txt", 1);
        string folder = MakeTree(dir);
        Junction(Path.Combine(folder, "Link"), outside.Path);

        var contents = FolderWipe.Scan(folder);
        Assert.Equal(3, contents.Files.Count); // nothing counted through the link
        var result = FolderWipe.Delete(contents, passes: 1, force: false);

        Assert.True(result.FolderRemoved);
        Assert.Equal(["precious.txt"], outside.Names()); // the link's target is untouched
    }

    [Fact]
    public void Delete_SelectedFolderIsAJunction_RemovesOnlyTheLink()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        outside.File("precious.txt", 1);
        string link = Path.Combine(dir.Path, "Link");
        Junction(link, outside.Path);

        var contents = FolderWipe.Scan(link);
        Assert.Empty(contents.Files);
        var result = FolderWipe.Delete(contents, passes: 1, force: false);

        Assert.True(result.FolderRemoved);
        Assert.False(Directory.Exists(link));
        Assert.Equal(["precious.txt"], outside.Names());
    }

    [Fact]
    public void Delete_FileInUse_KeepsOnlyWhatHoldsIt()
    {
        using var dir = new TempDir();
        string folder = MakeTree(dir);
        string busy = Path.Combine(folder, @"Sub\middle.txt");

        FolderDeleteResult result;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
            result = FolderWipe.Delete(FolderWipe.Scan(folder), passes: 1, force: false);

        Assert.False(result.FolderRemoved);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Contains(result.Failures, f => f.Path == busy);
        Assert.Contains(result.Failures, f => f.Path == folder && f.Error!.Contains("kept"));
        // Only the busy file and the folders holding it remain.
        Assert.Equal(["Secrets/Sub/middle.txt"], dir.AllNames());
        Assert.False(Directory.Exists(Path.Combine(folder, @"Sub\Deeper")));
        Assert.False(Directory.Exists(Path.Combine(folder, "Empty")));
    }

    [Fact]
    public void Delete_ReadOnlyFile_NeedsForce()
    {
        using var dir = new TempDir();
        string folder = MakeTree(dir);
        File.SetAttributes(Path.Combine(folder, "top.txt"), FileAttributes.ReadOnly);
        new DirectoryInfo(Path.Combine(folder, "Sub")).Attributes |= FileAttributes.ReadOnly; // a read-only folder is fine

        Assert.False(FolderWipe.Delete(FolderWipe.Scan(folder), 1, force: false).FolderRemoved);
        Assert.True(FolderWipe.Delete(FolderWipe.Scan(folder), 1, force: true).FolderRemoved);
    }

    [Fact]
    public void WhyNotAllowed_ProtectsDrivesAndSystemFolders()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.NotNull(FolderWipe.WhyNotAllowed(Path.GetPathRoot(windows)!));               // C:\
        Assert.NotNull(FolderWipe.WhyNotAllowed(windows));
        Assert.NotNull(FolderWipe.WhyNotAllowed(Path.Combine(windows, "System32")));        // inside Windows
        Assert.NotNull(FolderWipe.WhyNotAllowed(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(FolderWipe.WhyNotAllowed(profile));
        Assert.NotNull(FolderWipe.WhyNotAllowed(Path.GetDirectoryName(profile)!));          // C:\Users

        using var dir = new TempDir();
        Assert.Null(FolderWipe.WhyNotAllowed(dir.Path));
        Assert.Null(FolderWipe.WhyNotAllowed(Path.Combine(profile, "Documents", "Old stuff")));
    }

    [Fact]
    public void WipeForm_ListsFolders_AndRefusesProtectedOnes()
    {
        using var dir = new TempDir();
        string folder = MakeTree(dir);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new WipeForm([folder, windows]);
                form.CreateControl();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!form.ScansFinished.IsCompleted && DateTime.UtcNow < deadline)
                    Application.DoEvents(); // let the scan's continuation run
                Assert.True(form.ScansFinished.IsCompleted);

                Assert.Equal([folder], form.PendingFolders); // Windows is listed but not allowed
                Assert.Empty(form.PendingFiles);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("UI thread failed: " + error);
    }
}
