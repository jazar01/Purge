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
            Description = "Keep files newer than this many days (age is from the modified time, or see --age-by).",
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
        var recurse = new Option<bool>("--recurse", "-r")
        {
            Description = "Also purge matching files in all subfolders. Keep-number and keep-days apply to each folder separately.",
        };
        var log = new Option<string>("--log")
        {
            Description = "Append a timestamped record of this run to this file. The log file itself is never purged.",
            HelpName = "file",
        };
        var minSize = SizeOption("--min-size", "Only consider files at least this big, e.g. 500, 10KB, 1.5MB, 2GB.");
        var maxSize = SizeOption("--max-size", "Only consider files at most this big, e.g. 500, 10KB, 1.5MB, 2GB.");
        var ageBy = new Option<AgeBy>("--age-by")
        {
            Description = "Measure age from the file's modified or created time.",
            DefaultValueFactory = _ => AgeBy.Modified,
            HelpName = "modified|created",
        };
        var fileSpec = new Argument<string>("FileSpec")
        {
            Description = @"Directory and file pattern of the files to purge, e.g. D:\Backups\Finance*.zip",
        };

        var root = new RootCommand("Advanced file purging: delete old files while always keeping a minimum number.")
        {
            keepNumber, keepDays, securityLevel, whatIf, force, prompt, recurse, minSize, maxSize, ageBy, log, fileSpec,
        };

        root.Validators.Add(r =>
        {
            long? min, max;
            try
            {
                min = r.GetValue(minSize);
                max = r.GetValue(maxSize);
            }
            catch (InvalidOperationException)
            {
                return; // a size didn't parse; that error is already reported
            }
            if (min > max)
                r.AddError("--min-size must not be larger than --max-size.");
        });

        root.SetAction(parseResult =>
        {
            var options = new PurgeOptions(
                FileSpec: parseResult.GetValue(fileSpec)!,
                KeepNumber: parseResult.GetValue(keepNumber),
                KeepDays: parseResult.GetValue(keepDays),
                SecurityLevel: parseResult.GetValue(securityLevel),
                WhatIf: parseResult.GetValue(whatIf),
                Force: parseResult.GetValue(force),
                Prompt: parseResult.GetValue(prompt),
                Recurse: parseResult.GetValue(recurse),
                Selection: new FileSelection(
                    AgeBy: parseResult.GetValue(ageBy),
                    MinSize: parseResult.GetValue(minSize),
                    MaxSize: parseResult.GetValue(maxSize)));

            // Console.ReadKey throws when input is redirected (scheduled tasks, pipes),
            // so only offer a key reader when there is a real console to read from.
            Func<char>? readKey = Console.IsInputRedirected ? null : () => Console.ReadKey(intercept: true).KeyChar;

            string? logPath = parseResult.GetValue(log);
            if (logPath is null)
                return new PurgeProcessor(Console.Out, Console.Error, readKey).Run(options);

            return RunWithLog(options, logPath, parseResult.Tokens.Select(t => t.Value), readKey);
        });

        return root;
    }

    private static int RunWithLog(PurgeOptions options, string logPath, IEnumerable<string> arguments, Func<char>? readKey)
    {
        LogFile logFile;
        try
        {
            logFile = LogFile.Open(logPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Error: cannot open log file {logPath}: {e.Message}");
            return ExitCodes.Error;
        }

        using (logFile)
        using (var output = new TeeWriter(Console.Out, logFile))
        using (var error = new TeeWriter(Console.Error, logFile, tag: "ERROR"))
        {
            string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";
            logFile.WriteLine($"=== Purge {version} started: Purge {string.Join(' ', arguments.Select(Quote))}");
            try
            {
                int code = new PurgeProcessor(output, error, readKey)
                    .Run(options with { ExcludeFile = logFile.FullPath });
                logFile.WriteLine($"=== Finished, exit code {code}");
                return code;
            }
            catch (Exception e)
            {
                logFile.WriteLine($"=== Failed: {e.GetType().Name}: {e.Message}", "ERROR");
                throw;
            }
        }
    }

    private static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;

    private static Option<long?> SizeOption(string name, string description) => new(name)
    {
        Description = description,
        HelpName = "size",
        CustomParser = result =>
        {
            string token = result.Tokens.Single().Value;
            if (Size.TryParse(token, out long bytes))
                return bytes;
            result.AddError($"'{token}' is not a valid size for {name}. Use a number with an optional unit: B, KB, MB, GB or TB.");
            return null;
        },
    };

    private static void RequireNonNegative(OptionResult result, string name)
    {
        if (result.GetValueOrDefault<int>() < 0)
            result.AddError($"{name} must be zero or greater.");
    }
}
