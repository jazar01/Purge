using System.Security.AccessControl;
using System.Security.Principal;
using PurgeWindow;

namespace Purge.Tests;

/// <summary>
/// Securely deleting a folder that holds a subfolder the user can't open (like the "Logs"
/// folder Windows Backup leaves in WindowsImageBackup), and stopping a delete partway.
/// </summary>
public class StopAndLockedFolderTests
{
    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    /// <summary>Stops the current user from listing <paramref name="folder"/> until disposed.</summary>
    private sealed class Locked : IDisposable
    {
        private readonly DirectoryInfo _dir;
        private readonly FileSystemAccessRule _rule;

        public Locked(string folder)
        {
            _dir = new DirectoryInfo(folder);
            _rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var security = _dir.GetAccessControl();
            security.AddAccessRule(_rule);
            _dir.SetAccessControl(security);
        }

        public void Dispose()
        {
            var security = _dir.GetAccessControl();
            security.RemoveAccessRule(_rule);
            _dir.SetAccessControl(security);
        }
    }

    /// <summary>"Box" with a.txt and a "Logs" subfolder holding b.txt.</summary>
    private static (string Box, string Logs) MakeBoxWithLogs(TempDir dir)
    {
        dir.File(@"Box\a.txt", 1);
        dir.File(@"Box\Logs\b.txt", 1);
        string box = Path.Combine(dir.Path, "Box");
        return (box, Path.Combine(box, "Logs"));
    }

    [Fact]
    public void LockedSubfolder_IsReportedAsLeft_InsteadOfStoppingTheDelete()
    {
        using var dir = new TempDir();
        var (box, logs) = MakeBoxWithLogs(dir);

        FolderDeleteResult result;
        using (new Locked(logs))
            result = FolderWipe.Delete(FolderWipe.Scan(box), passes: 1, force: false);

        Assert.False(result.FolderRemoved);
        Assert.False(result.Stopped);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(Path.Combine(box, "a.txt")));
        var left = Assert.Single(result.Failures, f => f.Path == logs);
        Assert.Equal("access denied", left.Error);
        Assert.Contains(result.Failures, f => f.Path == box && f.Error == FolderWipe.KeptMessage);
    }

    [Fact]
    public void StoppedBeforeStarting_LeavesEveryFile_AndSaysWhy()
    {
        using var dir = new TempDir();
        string a = dir.File(@"Box\a.txt", 1);
        string b = dir.File(@"Box\Inner\b.txt", 1);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = FolderWipe.Delete(FolderWipe.Scan(Path.Combine(dir.Path, "Box")), 1, false, cancel: stop.Token);

        Assert.True(result.Stopped);
        Assert.False(result.FolderRemoved);
        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal([a, b], result.Failures.Select(f => f.Path).Order());
        Assert.All(result.Failures, f => Assert.Equal(SecureDelete.StoppedBefore, f.Error));
    }

    [Fact]
    public void StoppingMidFile_StopsAtOnce_AndLeavesTheFilePartlyOverwritten()
    {
        using var dir = new TempDir();
        string path = dir.File("big.bin", 1);
        File.WriteAllBytes(path, new byte[8 * 1024 * 1024]);   // 8 MB of zeros
        using var stop = new CancellationTokenSource();
        long written = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FileWiper.WipeAndDelete(new FileInfo(path), passes: 3,
                new Callback<long>(n => { written = n; stop.Cancel(); }), stop.Token));

        Assert.Equal(1024 * 1024, written);                   // stopped after the first 1 MB
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(8 * 1024 * 1024, after.Length);          // not truncated or deleted
        Assert.Contains(after.Take(1024 * 1024), b => b != 0); // the start is overwritten
        Assert.All(after.Skip(1024 * 1024), b => Assert.Equal(0, b)); // the rest is untouched
    }

    [Fact]
    public void FolderStoppedMidFile_SaysWhichFileIsPartlyOverwritten()
    {
        using var dir = new TempDir();
        string big = dir.File(@"Box\1-big.bin", 1);
        File.WriteAllBytes(big, new byte[4 * 1024 * 1024]);
        string after = dir.File(@"Box\2-after.txt", 1);
        using var stop = new CancellationTokenSource();

        // Many passes so the first file takes longer than one progress interval;
        // the first progress report during it stops the delete.
        var result = FolderWipe.Delete(FolderWipe.Scan(Path.Combine(dir.Path, "Box")), passes: 500, force: false,
            new Callback<WipeProgress>(p => { if (p.BytesWritten > 0) stop.Cancel(); }), stop.Token);

        Assert.True(result.Stopped);
        Assert.True(File.Exists(big) && File.Exists(after));
        Assert.Contains(result.Failures, f => f.Path == big && f.Error == SecureDelete.StoppedWhileOverwriting);
        Assert.Contains(result.Failures, f => f.Path == after && f.Error == SecureDelete.StoppedBefore);
    }

    /// <summary>Runs a WipeForm on an STA thread, shown off-screen as the list needs.</summary>
    private static void InWindow(string[] paths, Action<WipeForm> act)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new WipeForm(paths);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10_000, -10_000);
                form.ShowInTaskbar = false;
                form.Show();
                Pump(() => form.ScansFinished.IsCompleted);
                act(form);
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

    private static void Pump(Func<bool> until, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(until(), "timed out");
    }

    [Fact]
    public void Window_LockedSubfolder_ListsItAsLeft_AndFinishes()
    {
        using var dir = new TempDir();
        var (box, logs) = MakeBoxWithLogs(dir);

        using (new Locked(logs))
        {
            InWindow([box], form =>
            {
                var delete = form.DeleteWithoutConfirmingAsync();
                Pump(() => delete.IsCompleted);
                delete.GetAwaiter().GetResult();

                Assert.False(form.IsDeleting);
                Assert.StartsWith("Partly deleted: 1 item left", form.StatusOf(box));
                Assert.Contains(form.LeftoverRows, r => r.Path == logs && r.Reason == "access denied");
            });
        }
    }

    [Fact]
    public void Window_Stop_StopsMidFile_ListsWhatWasLeft_AndUnlocks()
    {
        using var dir = new TempDir();
        string big = dir.File(@"Box\1-big.bin", 1);
        File.WriteAllBytes(big, new byte[16 * 1024 * 1024]);
        string after = dir.File(@"Box\2-after.txt", 1);
        string box = Path.Combine(dir.Path, "Box");

        InWindow([box], form =>
        {
            form.Passes = 50;   // 800 MB of writing: plenty of time to stop it partway
            var delete = form.DeleteWithoutConfirmingAsync();
            Pump(() => form.StatusOf(box).EndsWith('%'));   // overwriting has visibly started
            Assert.True(form.IsDeleting);
            Render(form, "deleting.png");

            form.Stop();
            Pump(() => delete.IsCompleted);
            delete.GetAwaiter().GetResult();

            Assert.False(form.IsDeleting);
            Assert.StartsWith("Stopped: 2 items left", form.StatusOf(box));
            Assert.Contains(form.LeftoverRows, r => r.Path == big && r.Reason == SecureDelete.StoppedWhileOverwriting);
            Assert.Contains(form.LeftoverRows, r => r.Path == after && r.Reason == SecureDelete.StoppedBefore);
            Assert.True(File.Exists(big) && File.Exists(after));
            Render(form, "stopped.png");
        });
    }

    /// <summary>Saves a picture of the window when PURGE_RENDER_DIR is set (for checking by eye).</summary>
    private static void Render(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("PURGE_RENDER_DIR") is not { Length: > 0 } renderDir)
            return;
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(renderDir, name));
    }
}
