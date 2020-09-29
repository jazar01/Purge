using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.Contracts;

namespace Purge
{
    /// <summary>
    /// Entrypoint for utility
    /// Get parameters and then call the purgeprocessor
    /// </summary>
    class Purge
    { 
        static int Main(string[] args)
        {
            var rootCommand = new RootCommand
            {
                new Option<int>(
                    aliases: new string[]{"--keep-number", "-n" },
                    getDefaultValue: () => 0,  
                    description: "Keep at least this number of files."),
                new Option<int>(
                    aliases: new string[]{"--keep-days", "-d" },
                    getDefaultValue: () => 0,
                    description: "Keep files that are newer than this number of days."),
                new Option<int>(
                    aliases: new string[]{ "--security-level", "-s" },
                    getDefaultValue: () => 0,
                    description: "Number of passes of security overwriting."),
                new Option<bool>(
                    aliases: new string[]{"--whatif", "-t"},
                    "Test mode, does not actually purge any files."),
                new Option<bool>(
                    aliases: new string[]{"--force", "-f"},
                    "Force deletion of read-only files."),
                new Option<bool>(
                    aliases: new string[]{"--prompt", "-p"},
                    "Prompt for confirmation before deleting each file."),

                new Argument<string>("FileSpec", 
                    description: "File search pattern. ex. test*.zip" )
            };

            rootCommand.Description = "Advance file purging";

            rootCommand.Handler = CommandHandler.Create<int, int, int, bool, bool, bool, string>
                ((keepNumber,
                keepDays,
                securityLevel,
                whatIf,
                force,
                prompt,
                fileSpec) =>
                {
                    Console.WriteLine($"Keep Number = {keepNumber}");
                    Console.WriteLine($"Keep Days = {keepDays}");
                    Console.WriteLine($"Security Level = {securityLevel}");
                    Console.WriteLine($"Whatif = {whatIf}");
                    Console.WriteLine($"Force = {force}");
                    Console.WriteLine($"Prompt = {prompt}");
                    Console.WriteLine($"File Spec = {fileSpec}");

                    if (securityLevel < 0 || securityLevel > 50)
                        Console.WriteLine("Security level must be in the range of 0-50");
                    else if (keepDays < 0)
                        Console.WriteLine("Keep days must be zero or greater");
                    else if (keepNumber < 0)
                        Console.WriteLine("Keep number must be zero or greater");
                    else
                    {
                        PurgeProcessor pp = new PurgeProcessor(keepNumber, keepDays, securityLevel, whatIf, force, prompt, fileSpec);
                    }
                });

            return rootCommand.InvokeAsync(args).Result;

        }
    }
}
