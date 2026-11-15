namespace PurgeWindow;

public enum LaunchMode
{
    /// <summary>No arguments: the setup window (add/remove the Explorer menu).</summary>
    Setup,

    /// <summary>--wipe &lt;files&gt;: securely delete the given files.</summary>
    Wipe,

    /// <summary>--folder &lt;folder&gt;: purge files in a folder by rules.</summary>
    Folder,

    /// <summary>--add-to-explorer: add the right-click commands, without a window.</summary>
    AddToExplorer,

    /// <summary>--remove-from-explorer: remove the right-click commands, without a window.</summary>
    RemoveFromExplorer,
}

/// <summary>The command line PurgeWindow.exe was started with (normally by Explorer).</summary>
public sealed record LaunchArgs(LaunchMode Mode, IReadOnlyList<string> Paths)
{
    public const string Usage =
        "PurgeWindow.exe                          Set up the Explorer right-click menu\n" +
        "PurgeWindow.exe --folder <folder>        Purge files in a folder\n" +
        "PurgeWindow.exe --wipe <file> [file...]  Securely delete files\n" +
        "PurgeWindow.exe --add-to-explorer        Add the right-click menu\n" +
        "PurgeWindow.exe --remove-from-explorer   Remove the right-click menu";

    /// <exception cref="ArgumentException">Unknown option.</exception>
    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new(LaunchMode.Setup, []);

        var paths = args.Skip(1).Select(NormalizePath).Where(p => p.Length > 0).ToList();
        return args[0].ToLowerInvariant() switch
        {
            "--wipe" => new(LaunchMode.Wipe, paths),
            "--folder" => new(LaunchMode.Folder, paths),
            "--add-to-explorer" => new(LaunchMode.AddToExplorer, []),
            "--remove-from-explorer" => new(LaunchMode.RemoveFromExplorer, []),
            _ => throw new ArgumentException($"Unknown option '{args[0]}'."),
        };
    }

    /// <summary>
    /// Repairs paths as Explorer passes them. For a drive root, Explorer's <c>"%1"</c>
    /// becomes <c>"E:\"</c>, and Windows reads the backslash as escaping the closing quote,
    /// so the program receives <c>E:"</c>.
    /// </summary>
    public static string NormalizePath(string arg)
    {
        string path = arg.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':')
            path += '\\';
        return path;
    }
}
