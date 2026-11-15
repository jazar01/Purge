using Microsoft.Win32;

namespace PurgeWindow;

/// <summary>
/// Adds and removes Purge's commands in the Explorer right-click menu. The entries are
/// written to the current user's part of the registry (HKCU\Software\Classes), so no
/// administrator rights are needed and other users are not affected.
/// </summary>
/// <remarks>
/// On Windows 11 these "classic" commands appear under "Show more options" (or with
/// Shift+right-click). The short Windows 11 menu only lists signed, packaged apps.
/// </remarks>
public static class ExplorerMenu
{
    public const string FileVerb = "Purge.SecureDelete";
    public const string FolderVerb = "Purge.PurgeFiles";

    private static readonly (string Parent, string Verb, string Text, string Arguments)[] Entries =
    [
        (@"*\shell", FileVerb, "Securely delete…", "--wipe \"%1\""),
        (@"Directory\shell", FileVerb, "Securely delete…", "--wipe \"%1\""),
        (@"Directory\shell", FolderVerb, "Purge files…", "--folder \"%1\""),
        (@"Directory\Background\shell", FolderVerb, "Purge files…", "--folder \"%V\""),
        (@"Drive\shell", FolderVerb, "Purge files…", "--folder \"%1\""),
    ];

    /// <summary>The current user's HKCU\Software\Classes key.</summary>
    public static RegistryKey OpenUserClasses() => Registry.CurrentUser.CreateSubKey(@"Software\Classes");

    /// <summary>Adds (or updates) the commands so they run <paramref name="exePath"/>.</summary>
    /// <param name="classes">The Classes key to write to; the current user's by default (tests pass their own).</param>
    public static void Add(string exePath, RegistryKey? classes = null)
    {
        using var owned = classes is null ? OpenUserClasses() : null;
        var root = classes ?? owned!;

        foreach (var entry in Entries)
        {
            using var verb = root.CreateSubKey($@"{entry.Parent}\{entry.Verb}");
            verb.SetValue("", entry.Text);
            verb.SetValue("Icon", exePath);
            if (entry.Verb == FileVerb)
            {
                // Offer "Securely delete" for any number of selected files (Explorer's
                // default hides it above 15). Each file starts PurgeWindow.exe, and the
                // copies hand their files to the first one, which shows a single window.
                verb.SetValue("MultiSelectModel", "Player");
            }

            using var command = verb.CreateSubKey("command");
            command.SetValue("", $"\"{exePath}\" {entry.Arguments}");
        }
    }

    /// <summary>Removes the commands. Does nothing if they are not there.</summary>
    public static void Remove(RegistryKey? classes = null)
    {
        using var owned = classes is null ? OpenUserClasses() : null;
        var root = classes ?? owned!;

        foreach (var entry in Entries)
            root.DeleteSubKeyTree($@"{entry.Parent}\{entry.Verb}", throwOnMissingSubKey: false);
    }

    /// <summary>
    /// True if every command is in the menu and runs <paramref name="exePath"/>; false if
    /// any is missing (for example, added by an older version) or runs another copy.
    /// </summary>
    public static bool IsUpToDate(string exePath, RegistryKey? classes = null)
    {
        using var owned = classes is null ? OpenUserClasses() : null;
        var root = classes ?? owned!;

        foreach (var entry in Entries)
        {
            using var command = root.OpenSubKey($@"{entry.Parent}\{entry.Verb}\command");
            if (command?.GetValue("") as string != $"\"{exePath}\" {entry.Arguments}")
                return false;
        }
        return true;
    }

    /// <summary>
    /// The PurgeWindow.exe that the menu currently runs, or null if the menu isn't added.
    /// </summary>
    public static string? InstalledExePath(RegistryKey? classes = null)
    {
        using var owned = classes is null ? OpenUserClasses() : null;
        var root = classes ?? owned!;

        using var command = root.OpenSubKey($@"*\shell\{FileVerb}\command");
        if (command?.GetValue("") is not string line || !line.StartsWith('"'))
            return null;

        int end = line.IndexOf('"', 1);
        return end > 1 ? line[1..end] : null;
    }
}
