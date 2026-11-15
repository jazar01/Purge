using PurgeWindow;

namespace Purge.Tests;

public class RemoveEmptyFoldersTests
{
    private static (int Code, string Output, PurgeProcessor Processor) Run(PurgeOptions options, Func<char>? readKey = null)
    {
        var output = new StringWriter();
        var processor = new PurgeProcessor(output, output, readKey);
        int code = processor.Run(options, TempDir.Now);
        return (code, output.ToString(), processor);
    }

    private static PurgeOptions Options(TempDir dir, bool remove = true, int keepNumber = 0, bool whatIf = false, bool prompt = false) =>
        new(dir.Spec("*.bak"), KeepNumber: keepNumber, KeepDays: 30, Recurse: true,
            RemoveEmptyFolders: remove, WhatIf: whatIf, Prompt: prompt);

    private static bool Exists(TempDir dir, string folder) => Directory.Exists(Path.Combine(dir.Path, folder));

    [Fact]
    public void EmptiedSubfolders_AreRemoved_DeepestFirst()
    {
        using var dir = new TempDir();
        dir.File(@"Jan\old.bak", 40);
        dir.File(@"Jan\Week1\old.bak", 45);
        dir.File(@"Feb\new.bak", 1);

        var (code, output, processor) = Run(Options(dir));

        Assert.Equal(ExitCodes.Success, code);
        Assert.False(Exists(dir, @"Jan\Week1"));
        Assert.False(Exists(dir, "Jan"));          // emptied once Week1 was removed
        Assert.True(Exists(dir, "Feb"));            // still has a recent file
        Assert.True(Directory.Exists(dir.Path));    // the named folder is never removed
        Assert.Equal(2, processor.FoldersRemoved);
        Assert.Contains("Folders removed: 2", output);
        Assert.True(output.IndexOf(@"Jan\Week1", StringComparison.Ordinal) < output.IndexOf("Removing empty folder: " + Path.Combine(dir.Path, "Jan") + Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void FoldersAlreadyEmpty_AreKept()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Waiting"));
        Directory.CreateDirectory(Path.Combine(dir.Path, @"Old\AlsoWaiting"));
        dir.File(@"Old\old.bak", 40);

        Run(Options(dir));

        Assert.True(Exists(dir, "Waiting"));
        Assert.True(Exists(dir, @"Old\AlsoWaiting"));
        Assert.True(Exists(dir, "Old")); // not empty: it still holds the untouched empty folder
        Assert.Equal(["Old/AlsoWaiting", "Waiting"], Directory.GetDirectories(dir.Path, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(dir.Path, d).Replace('\\', '/')).Where(d => d.Contains('/') || d == "Waiting").Order().ToArray());
    }

    [Fact]
    public void FolderWithOtherFiles_IsKept()
    {
        using var dir = new TempDir();
        dir.File(@"Mixed\old.bak", 40);
        dir.File(@"Mixed\readme.txt", 40); // doesn't match the pattern

        Run(Options(dir));

        Assert.Equal(["Mixed/readme.txt"], dir.AllNames());
    }

    [Fact]
    public void FilesKeptByRules_KeepTheirFolder()
    {
        using var dir = new TempDir();
        dir.File(@"Sub\a.bak", 40);
        dir.File(@"Sub\b.bak", 50);

        Run(Options(dir, keepNumber: 1));

        Assert.Equal(["Sub/a.bak"], dir.AllNames());
    }

    [Fact]
    public void WithoutTheOption_FoldersStay()
    {
        using var dir = new TempDir();
        dir.File(@"Sub\old.bak", 40);

        var (_, output, _) = Run(Options(dir, remove: false));

        Assert.True(Exists(dir, "Sub"));
        Assert.DoesNotContain("Folders removed", output);
    }

    [Fact]
    public void WhatIf_ReportsTheCascade_WithoutRemovingAnything()
    {
        using var dir = new TempDir();
        dir.File(@"Jan\old.bak", 40);
        dir.File(@"Jan\Week1\old.bak", 45);

        var (_, output, processor) = Run(Options(dir, whatIf: true));

        Assert.Contains("Would remove empty folder: " + Path.Combine(dir.Path, @"Jan\Week1"), output);
        Assert.Contains("Would remove empty folder: " + Path.Combine(dir.Path, "Jan"), output);
        Assert.Contains("Folders that would be removed: 2", output);
        Assert.Equal(2, processor.FoldersRemoved);
        Assert.Equal(2, dir.AllNames().Length);   // nothing was deleted
        Assert.True(Exists(dir, @"Jan\Week1"));
    }

    [Fact]
    public void FileSkippedAtThePrompt_KeepsItsFolder()
    {
        using var dir = new TempDir();
        dir.File(@"Sub\old.bak", 40);

        Run(Options(dir, prompt: true), readKey: () => 'n');

        Assert.Equal(["Sub/old.bak"], dir.AllNames());
    }

    [Fact]
    public void CommandLine_RequiresRecurse()
    {
        Assert.NotEmpty(Program.BuildRootCommand().Parse(["--remove-empty-folders", "*.bak"]).Errors);
        Assert.Empty(Program.BuildRootCommand().Parse(["-r", "--remove-empty-folders", "*.bak"]).Errors);
    }

    [Fact]
    public void PurgeWindow_PassesTheOption_OnlyWithSubfolders()
    {
        using var dir = new TempDir();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new PurgeForm(dir.Path);
                form.SetPattern("*.bak");

                form.SetSubfolders(recurse: true, removeEmpty: true);
                Assert.True(form.BuildOptions(whatIf: true).Options!.RemoveEmptyFolders);

                form.SetSubfolders(recurse: false, removeEmpty: true);
                Assert.False(form.BuildOptions(whatIf: true).Options!.RemoveEmptyFolders);
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
