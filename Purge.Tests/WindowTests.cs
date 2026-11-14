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

        ExplorerMenu.Remove(_classes);

        Assert.Null(ExplorerMenu.InstalledExePath(_classes));
        Assert.Null(_classes.OpenSubKey(@"Directory\shell\Purge.PurgeFiles"));
        Assert.Null(_classes.OpenSubKey(@"Drive\shell\Purge.PurgeFiles"));
        ExplorerMenu.Remove(_classes); // removing twice is fine
    }
}

public class SelectionCollectorTests
{
    [Fact]
    public async Task OtherCopies_HandTheirFilesToThePrimary()
    {
        string channel = "Purge.Test." + Guid.NewGuid().ToString("N");
        using var primary = SelectionCollector.TryBecomePrimary(channel);
        Assert.NotNull(primary);
        Assert.Null(SelectionCollector.TryBecomePrimary(channel)); // only one primary

        var received = new List<string>();
        var collecting = primary.CollectAsync(p => { lock (received) received.Add(p); }, TimeSpan.FromSeconds(1));

        // Several "copies" starting at once, as when Explorer opens one per selected file.
        var senders = Enumerable.Range(1, 8)
            .Select(i => Task.Run(() => SelectionCollector.TrySend(channel, [$@"C:\file{i}.txt"])))
            .ToArray();
        Assert.All(await Task.WhenAll(senders), Assert.True);

        await collecting;
        Assert.Equal(8, received.Distinct().Count());

        // Once collecting is over, a new right-click becomes a new primary.
        using var next = SelectionCollector.TryBecomePrimary(channel);
        Assert.NotNull(next);
    }

    [Fact]
    public void NoPrimary_SendFails() =>
        Assert.False(SelectionCollector.TrySend("Purge.Test." + Guid.NewGuid().ToString("N"), ["x"], timeoutMs: 200));
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
            Assert.Equal((5, 30, 0, true), (options.KeepNumber, options.KeepDays, options.SecurityLevel, options.WhatIf));
        });
    }
}
