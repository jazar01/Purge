using System.CommandLine;
using System.CommandLine.Parsing;

namespace Purge;

/// <summary>
/// Entry point: parse the command line, then hand off to <see cref="PurgeProcessor"/>.
/// </summary>
public static class Program
{
    public static int Main(string[] args) => BuildRootCommand().Parse(args).Invoke();

    public static RootCommand BuildRootCommand()
    {
        var keepNumber = new Option<int>("--keep-number", "-n")
        {
            Description = "Always keep at least this many of the newest files.",
            DefaultValueFactory = _ => 0,
        };
        keepNumber.Validators.Add(r => RequireNonNegative(r, "Keep number"));

        var keepDays = new Option<int>("--keep-days", "-d")
        {
            Description = "Keep files modified within this many days.",
            DefaultValueFactory = _ => 0,
        };
        keepDays.Validators.Add(r => RequireNonNegative(r, "Keep days"));

        var securityLevel = new Option<int>("--security-level", "-l", "-s")
        {
            Description = "Number of passes of random overwriting before deleting (0-50).",
            DefaultValueFactory = _ => 0,
        };
        securityLevel.Validators.Add(r =>
        {
            int value = r.GetValueOrDefault<int>();
            if (value < 0 || value > 50)
                r.AddError("Security level must be in the range of 0-50.");
        });

        var whatIf = new Option<bool>("--whatif", "-t")
        {
            Description = "Test mode: show what would be purged without deleting anything.",
        };
        var force = new Option<bool>("--force", "-f")
        {
            Description = "Force deletion of read-only files.",
        };
        var prompt = new Option<bool>("--prompt", "-p")
        {
            Description = "Prompt for confirmation before deleting each file.",
        };
        var fileSpec = new Argument<string>("FileSpec")
        {
            Description = @"Directory and file pattern of the files to purge, e.g. D:\Backups\Finance*.zip",
        };

        var root = new RootCommand("Advanced file purging: delete old files while always keeping a minimum number.")
        {
            keepNumber, keepDays, securityLevel, whatIf, force, prompt, fileSpec,
        };

        root.SetAction(parseResult =>
        {
            var options = new PurgeOptions(
                FileSpec: parseResult.GetValue(fileSpec)!,
                KeepNumber: parseResult.GetValue(keepNumber),
                KeepDays: parseResult.GetValue(keepDays),
                SecurityLevel: parseResult.GetValue(securityLevel),
                WhatIf: parseResult.GetValue(whatIf),
                Force: parseResult.GetValue(force),
                Prompt: parseResult.GetValue(prompt));

            // Console.ReadKey throws when input is redirected (scheduled tasks, pipes),
            // so only offer a key reader when there is a real console to read from.
            Func<char>? readKey = Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true).KeyChar;

            return new PurgeProcessor(Console.Out, Console.Error, readKey).Run(options);
        });

        return root;
    }

    private static void RequireNonNegative(OptionResult result, string name)
    {
        if (result.GetValueOrDefault<int>() < 0)
            result.AddError($"{name} must be zero or greater.");
    }
}
