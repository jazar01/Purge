using System.Diagnostics;
using PurgeWindow;

namespace Purge.Tests;

public class LeftoverReportTests
{
    /// <summary>A folder "Box" with two files in a subfolder.</summary>
    private static string MakeBox(TempDir dir)
    {
        dir.File(@"Box\a.txt", 1);
        dir.File(@"Box\Inner\b.txt", 1);
        return Path.Combine(dir.Path, "Box");
    }

    /// <summary>
    /// Starts a process whose working directory is <paramref name="folder"/>, which stops
    /// Windows from removing that folder (as a terminal open in it would).
    /// </summary>
    private static Process HoldFolder(string folder)
    {
        // ping.exe itself, not via cmd.exe: one process, so stopping it really frees the folder.
        var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        Thread.Sleep(300);
        return p;
    }

    private static void Release(Process p)
    {
        try { p.Kill(); } catch (InvalidOperationException) { }
        p.WaitForExit();
        p.Dispose();
        Thread.Sleep(100); // let Windows finish releasing the folder handle
    }

    [Fact]
    public void FileAddedAfterTheScan_IsReported()
    {
        using var dir = new TempDir();
        string box = MakeBox(dir);
        var contents = FolderWipe.Scan(box);
        string late = dir.File(@"Box\Inner\late.txt", 1); // appears after the scan

        var result = FolderWipe.Delete(contents, passes: 1, force: false);

        Assert.False(result.FolderRemoved);
        Assert.Contains(result.Failures, f => f.Path == late && f.Error!.Contains("after the folder was scanned"));
        Assert.Contains(result.Failures, f => f.Path == box && f.Error == FolderWipe.KeptMessage);
    }

    [Fact]
    public void FolderItselfInUse_ReportsWhy()
    {
        using var dir = new TempDir();
        string box = MakeBox(dir);
        var holder = HoldFolder(box);
        try
        {
            var result = FolderWipe.Delete(FolderWipe.Scan(box), passes: 0, force: false);

            Assert.False(result.FolderRemoved);
            var self = Assert.Single(result.Failures);   // nothing inside is left, only the folder
            Assert.Equal(box, self.Path);
            Assert.NotEqual(FolderWipe.KeptMessage, self.Error);
            Assert.False(string.IsNullOrWhiteSpace(self.Error));
        }
        finally
        {
            Release(holder);
        }
    }

    /// <summary>Runs a WipeForm on an STA thread, deletes without the confirmation box, and inspects it.</summary>
    private static void DeleteInWindow(string[] paths, Action<WipeForm> inspect)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new WipeForm(paths);
                // Like the real window, the list must be on screen (here, off to the side).
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10_000, -10_000);
                form.ShowInTaskbar = false;
                form.Show();
                Pump(form.ScansFinished);
                Pump(form.DeleteWithoutConfirmingAsync());
                inspect(form);
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

        static void Pump(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                Application.DoEvents();
            Assert.True(task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Window_ListsEachItemLeft_WithItsReason()
    {
        using var dir = new TempDir();
        string box = MakeBox(dir);
        string locked = Path.Combine(box, @"Inner\b.txt");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            DeleteInWindow([box], form =>
            {
                Assert.StartsWith("Partly deleted: 1 item left", form.StatusOf(box));
                var row = Assert.Single(form.LeftoverRows);
                Assert.Equal(locked, row.Path);
                Assert.StartsWith("in use by another program", row.Reason);

                if (Environment.GetEnvironmentVariable("PURGE_RENDER_DIR") is { Length: > 0 } renderDir)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(renderDir, "leftovers.png"));
                }
            });
        }
    }

    [Fact]
    public void Window_FolderInUse_SaysWhyInsteadOfACount()
    {
        using var dir = new TempDir();
        string box = MakeBox(dir);
        var holder = HoldFolder(box);
        try
        {
            DeleteInWindow([box], form =>
            {
                string status = form.StatusOf(box);
                Assert.StartsWith("Not removed: ", status);
                Assert.True(status.Length > "Not removed: ".Length + 5, status);
                Assert.Empty(form.LeftoverRows);
            });
        }
        finally
        {
            Release(holder);
        }
    }
}
