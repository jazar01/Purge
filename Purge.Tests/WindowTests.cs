using Microsoft.Win32;
using Purge;
using PurgeWindow;

namespace Purge.Tests;

public class SecureDeleteTests
{
    [Fact]
    public void DeletesFile()
    {
        using var dir = new TempDir();
        string path = dir.File("secret.txt", 1, "top secret");

        var result = SecureDelete.Delete(path, passes: 2, force: false);

        Assert.True(result.Deleted);
        Assert.Empty(dir.Names());
    }

    [Fact]
    public void ReadOnly_NeedsForce()
    {
        using var dir = new TempDir();
        string path = dir.File("locked.txt", 1);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Equal("read-only", SecureDelete.Delete(path, 1, force: false).Error);
        Assert.Single(dir.Names());

        Assert.True(SecureDelete.Delete(path, 1, force: true).Deleted);
        Assert.Empty(dir.Names());
    }

    [Fact]
    public void MissingFileAndFolder_AreReportedNotThrown()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));

        Assert.Equal("file not found", SecureDelete.Delete(Path.Combine(dir.Path, "nope.txt"), 1, false).Error);
        Assert.Contains("folder", SecureDelete.Delete(Path.Combine(dir.Path, "sub"), 1, false).Error);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "sub")));
    }

    [Fact]
    public void PurgeProcessor_ReportsCounts()
    {
        using var dir = new TempDir();
        dir.File("a.bak", 40);
        dir.File("b.bak", 50);
        dir.File("c.bak", 1);

        var processor = new PurgeProcessor(TextWriter.Null, TextWriter.Null);
        processor.Run(new PurgeOptions(dir.Spec("*.bak"), KeepDays: 30, WhatIf: true), TempDir.Now);

        Assert.Equal((2, 1, 0), (processor.Purged, processor.Retained, processor.Failed));
    }
}

public class LaunchArgsTests
{
    [Fact]
    public void NoArguments_IsSetup() => Assert.Equal(LaunchMode.Setup, LaunchArgs.Parse([]).Mode);

    [Fact]
    public void Wipe_TakesFiles()
    {
        var args = LaunchArgs.Parse(["--wipe", @"C:\a.txt", @"C:\b.txt"]);
        Assert.Equal(LaunchMode.Wipe, args.Mode);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], args.Paths);
    }

    [Theory]
    [InlineData("E:\"", @"E:\")]          // Explorer's "%1" for a drive root arrives like this
    [InlineData("E:", @"E:\")]
    [InlineData(@"D:\Backups", @"D:\Backups")]
    [InlineData("\"D:\\My Backups\"", @"D:\My Backups")]
    public void Folder_PathsAreRepaired(string arg, string expected) =>
        Assert.Equal(expected, LaunchArgs.Parse(["--folder", arg]).Paths.Single());

    [Fact]
    public void UnknownOption_Throws() => Assert.Throws<ArgumentException>(() => LaunchArgs.Parse(["--bogus"]));
}

public sealed class ExplorerMenuTests : IDisposable
{
    // A throwaway key stands in for HKCU\Software\Classes.
    private readonly string _keyPath = @"Software\PurgeTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _classes;

    public ExplorerMenuTests() => _classes = Registry.CurrentUser.CreateSubKey(_keyPath);

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
    }

    [Fact]
    public void AddThenRemove()
    {
        const string exe = @"C:\Tools\Purge\PurgeWindow.exe";
        Assert.Null(ExplorerMenu.InstalledExePath(_classes));

        ExplorerMenu.Add(exe, _classes);

        Assert.Equal(exe, ExplorerMenu.InstalledExePath(_classes));
        Assert.Equal($"\"{exe}\" --wipe \"%1\"", _classes.OpenSubKey(@"*\shell\Purge.SecureDelete\command")!.GetValue(""));
        Assert.Equal("Player", _classes.OpenSubKey(@"*\shell\Purge.SecureDelete")!.GetValue("MultiSelectModel"));
        Assert.Equal($"\"{exe}\" --folder \"%1\"", _classes.OpenSubKey(@"Directory\shell\Purge.PurgeFiles\command")!.GetValue(""));
        Assert.Equal($"\"{exe}\" --folder \"%V\"", _classes.OpenSubKey(@"Directory\Background\shell\Purge.PurgeFiles\command")!.GetValue(""));
        Assert.Equal($"\"{exe}\" --folder \"%1\"", _classes.OpenSubKey(@"Drive\shell\Purge.PurgeFiles\command")!.GetValue(""));
        Assert.Equal($"\"{exe}\" --wipe \"%1\"", _classes.OpenSubKey(@"Directory\shell\Purge.SecureDelete\command")!.GetValue(""));
        Assert.Null(_classes.OpenSubKey(@"Drive\shell\Purge.SecureDelete")); // never offered for whole drives
        Assert.True(ExplorerMenu.IsUpToDate(exe, _classes));
        Assert.False(ExplorerMenu.IsUpToDate(@"D:\Other\PurgeWindow.exe", _classes));

        // A menu added by an older version (without the folder command) needs updating.
        _classes.DeleteSubKeyTree(@"Directory\shell\Purge.SecureDelete");
        Assert.False(ExplorerMenu.IsUpToDate(exe, _classes));

        ExplorerMenu.Remove(_classes);

        Assert.Null(ExplorerMenu.InstalledExePath(_classes));
        Assert.Null(_classes.OpenSubKey(@"Directory\shell\Purge.PurgeFiles"));
        Assert.Null(_classes.OpenSubKey(@"Directory\shell\Purge.SecureDelete"));
        Assert.Null(_classes.OpenSubKey(@"Drive\shell\Purge.PurgeFiles"));
        ExplorerMenu.Remove(_classes); // removing twice is fine
    }
}

public class SelectionCollectorTests
{
    private static string NewChannel() => "Purge.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Reads what the primary has received so far, waiting briefly for <paramref name="count"/> items.</summary>
    private static async Task<List<string>> Received(SelectionCollector primary, int count)
    {
        var received = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < count)
            received.Add(await primary.Paths.ReadAsync(timeout.Token));
        return received;
    }

    [Fact]
    public async Task OtherCopies_HandTheirItemsToThePrimary()
    {
        string channel = NewChannel();
        using var primary = SelectionCollector.TryBecomePrimary(channel);
        Assert.NotNull(primary);
        Assert.Null(SelectionCollector.TryBecomePrimary(channel)); // only one primary

        // Many "copies" at once, as when Explorer starts one per selected item.
        var senders = Enumerable.Range(1, 30)
            .Select(i => Task.Run(() => SelectionCollector.TrySend(channel, [$@"C:\file{i}.txt"])))
            .ToArray();
        Assert.All(await Task.WhenAll(senders), Assert.True);

        Assert.Equal(30, (await Received(primary, 30)).Distinct().Count());
    }

    [Fact]
    public async Task HandOff_DoesNotWaitForTheWindow()
    {
        // The primary's window may be slow to open (e.g. waiting for a disk to spin up) and
        // not reading anything yet; copies are still taken straight away.
        string channel = NewChannel();
        using var primary = SelectionCollector.TryBecomePrimary(channel)!;
        await Task.Delay(500);   // nobody reads Paths meanwhile

        var copies = Enumerable.Range(1, 20)
            .Select(i => Task.Run(() => SelectionCollector.Join(channel, [$@"C:\big{i}.bin"], TimeSpan.FromSeconds(30))))
            .ToArray();
        var results = await Task.WhenAll(copies);

        Assert.All(results, r => Assert.True(r.HandedOff));    // no extra windows
        Assert.Equal(20, (await Received(primary, 20)).Distinct().Count());
    }

    [Fact]
    public async Task PrimaryStillHolding_ButNotListeningYet_CopyWaitsInsteadOfOpeningItsOwnWindow()
    {
        string channel = NewChannel();
        // Something holds the primary's place for 2.5 s without listening (the old failure:
        // copies gave up after 3 s). When it lets go, the copy should take over, not give up.
        var holder = Task.Run(async () =>
        {
            using var gate = new Semaphore(1, 1, @"Local\" + channel);
            gate.WaitOne();
            await Task.Delay(2500);
            gate.Release();
        });
        await Task.Delay(200);

        var (handedOff, collector) = await Task.Run(() => SelectionCollector.Join(channel, [@"C:\x.txt"], TimeSpan.FromSeconds(30)));
        using (collector)
        {
            Assert.False(handedOff);
            Assert.NotNull(collector);   // became the primary once the place was free
        }
        await holder;
    }

    [Fact]
    public async Task AfterStopListening_CopiesAreRefused_AndANewRightClickGetsItsOwnWindow()
    {
        string channel = NewChannel();
        using var primary = SelectionCollector.TryBecomePrimary(channel)!;
        primary.StopListening();   // deleting has started

        Assert.False(SelectionCollector.TrySend(channel, ["late"], timeoutMs: 300));
        var (handedOff, next) = await Task.Run(() => SelectionCollector.Join(channel, ["late"], TimeSpan.FromSeconds(10)));
        using (next)
        {
            Assert.False(handedOff);
            Assert.NotNull(next);
        }
    }

    [Fact]
    public void NoWindowReachable_CopyGivesUpAfterTheLimit()
    {
        string channel = NewChannel();
        using var gate = new Semaphore(1, 1, @"Local\" + channel);
        gate.WaitOne();   // a "primary" that never listens
        try
        {
            var (handedOff, collector) = SelectionCollector.Join(channel, ["x"], TimeSpan.FromMilliseconds(500));
            Assert.False(handedOff);
            Assert.Null(collector);   // shows a window of its own rather than losing the item
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public void NoPrimary_SendFails() =>
        Assert.False(SelectionCollector.TrySend(NewChannel(), ["x"], timeoutMs: 200));

    [Fact]
    public void Window_KeepsCollectingUntilDelete_ThenLateItemsAreRefused()
    {
        using var dir = new TempDir();
        string first = dir.File("first.txt", 1);
        string later = dir.File("later.txt", 1);
        string channel = NewChannel();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var collector = SelectionCollector.TryBecomePrimary(channel)!;
                using var form = new WipeForm([first], collector);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10_000, -10_000);
                form.ShowInTaskbar = false;
                form.Show();

                // Long after the first item: still taken into the same window.
                Pump(null, TimeSpan.FromSeconds(2.5));
                var send = Task.Run(() => SelectionCollector.TrySend(channel, [later]));
                Pump(() => send.IsCompleted);
                Assert.True(send.Result);
                Pump(() => form.PendingFiles.Count == 2);

                var delete = form.DeleteWithoutConfirmingAsync();
                Pump(() => delete.IsCompleted);
                delete.GetAwaiter().GetResult();
                Assert.False(File.Exists(first) || File.Exists(later));

                // Once deleting has started, a new right-click gets its own window.
                Assert.False(SelectionCollector.TrySend(channel, ["new"], timeoutMs: 300));
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

        static void Pump(Func<bool>? until = null, TimeSpan? time = null)
        {
            var deadline = DateTime.UtcNow + (time ?? TimeSpan.FromSeconds(20));
            while ((until is null || !until()) && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            if (until is not null)
                Assert.True(until(), "timed out");
        }
    }
}

public class WindowTests
{
    /// <summary>Runs code on an STA thread, as Windows Forms requires.</summary>
    private static void OnUiThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("UI thread failed: " + error);
    }

    /// <summary>
    /// Lays the window out and draws it. Set PURGE_RENDER_DIR to also save a PNG of each
    /// window, to check the layout by eye.
    /// </summary>
    private static void Render(Form form, string name)
    {
        // Controls are only drawn once the window is shown, so show it off-screen.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10_000, -10_000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        if (Environment.GetEnvironmentVariable("PURGE_RENDER_DIR") is { Length: > 0 } dir)
            bitmap.Save(Path.Combine(dir, name + ".png"));
    }

    [Fact]
    public void Windows_Render()
    {
        using var dir = new TempDir();
        string a = dir.File("Finance Backup 2026-10-01.zip", 44, new string('x', 5000));
        string b = dir.File("notes.txt", 2);
        Directory.CreateDirectory(Path.Combine(dir.Path, "Old"));

        OnUiThread(() =>
        {
            using (var wipe = new WipeForm([a, b, Path.Combine(dir.Path, "Old"), Path.Combine(dir.Path, "gone.txt")]))
            {
                Render(wipe, "wipe");
                Assert.Equal([a, b], wipe.PendingFiles);
            }

            using (var purge = new PurgeForm(dir.Path))
            {
                purge.SetPattern("*.zip");
                Render(purge, "purge");
            }

            using (var setup = new SetupForm(@"C:\Tools\Purge\PurgeWindow.exe"))
                Render(setup, "setup");
        });
    }

    [Theory]
    [InlineData("", "Enter which files")]
    [InlineData(@"sub\*.bak", "only a file name pattern")]
    public void PurgeForm_ValidatesPattern(string pattern, string expectedError)
    {
        using var dir = new TempDir();
        OnUiThread(() =>
        {
            using var form = new PurgeForm(dir.Path);
            form.SetPattern(pattern);
            var (options, error) = form.BuildOptions(whatIf: true);
            Assert.Null(options);
            Assert.Contains(expectedError, error);
        });
    }

    [Fact]
    public void PurgeForm_BuildsOptions()
    {
        using var dir = new TempDir();
        OnUiThread(() =>
        {
            using var form = new PurgeForm(dir.Path);
            form.SetPattern("*.bak");
            var (options, error) = form.BuildOptions(whatIf: true);
            Assert.Null(error);
            Assert.Equal(Path.Combine(dir.Path, "*.bak"), options!.FileSpec);
            Assert.Equal((0, 0, 1, true), (options.KeepNumber, options.KeepDays, options.SecurityLevel, options.WhatIf));
        });
    }
}
