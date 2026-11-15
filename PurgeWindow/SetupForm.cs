namespace PurgeWindow;

/// <summary>
/// Shown when PurgeWindow.exe is opened directly: add or remove the Explorer menu, or
/// use the purge and secure-delete windows without it.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly string _exePath;
    private readonly Label _menuStatus;
    private readonly Button _add;
    private readonly Button _remove;

    public SetupForm(string? exePath = null)
    {
        _exePath = exePath ?? Environment.ProcessPath!;
        string version = typeof(SetupForm).Assembly.GetName().Version?.ToString(3) ?? "";

        Text = "Purge " + version;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;

        const int textWidth = 470;
        var heading = Ui.Label("Explorer right-click menu", textWidth);
        heading.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);

        var explain = Ui.Label(
            "Adds two commands to the right-click menu in File Explorer:\n\n" +
            "  •  Purge files…  on a folder: delete old files by pattern, always keeping the newest.\n" +
            "  •  Securely delete…  on files and folders: overwrite every file and delete it, and\n" +
            "      remove the selected folders with everything in them.\n\n" +
            "On Windows 11 they are under \"Show more options\" (or Shift+right-click). " +
            "The menu runs this copy of PurgeWindow.exe, so add it again if you move the file.", textWidth);

        _menuStatus = Ui.Label("", textWidth);
        _add = Ui.Button("Add to menu", (_, _) => Change(add: true));
        _remove = Ui.Button("Remove from menu", (_, _) => Change(add: false));

        var useHeading = Ui.Label("Use without the menu", textWidth);
        useHeading.Font = heading.Font;
        var purgeFolder = Ui.Button("Purge files in a folder…", (_, _) => new PurgeForm().Show(this));
        var wipeFiles = Ui.Button("Securely delete files…", (_, _) => PickFilesToWipe());
        var wipeFolder = Ui.Button("Securely delete a folder…", (_, _) => PickFolderToWipe());
        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        layout.Controls.Add(heading);
        layout.Controls.Add(explain);
        layout.Controls.Add(_menuStatus);
        layout.Controls.Add(Ui.Row(_add, _remove));
        layout.Controls.Add(useHeading);
        layout.Controls.Add(Ui.Row(purgeFolder, wipeFiles, wipeFolder));
        layout.Controls.Add(Ui.ButtonBar(close));
        useHeading.Margin = new Padding(3, 18, 3, 6);
        Controls.Add(layout);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string? installed = ExplorerMenu.InstalledExePath();
        bool isThisCopy = installed is not null && string.Equals(installed, _exePath, StringComparison.OrdinalIgnoreCase);
        bool upToDate = isThisCopy && ExplorerMenu.IsUpToDate(_exePath);

        _menuStatus.Text = installed is null
            ? "The commands are not in the menu."
            : upToDate
                ? "✔  The commands are in the menu."
                : isThisCopy
                    ? "The menu is from an older version. Click Update to add the new commands."
                    : $"The menu runs another copy: {installed}\nClick Update to use this copy instead.";
        _add.Text = installed is null ? "Add to menu" : "Update menu";
        _add.Enabled = !upToDate;
        _remove.Enabled = installed is not null;
    }

    private void Change(bool add)
    {
        try
        {
            if (add)
                ExplorerMenu.Add(_exePath);
            else
                ExplorerMenu.Remove();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, "Could not change the menu: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshStatus();
    }

    private void PickFilesToWipe()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Files to securely delete" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            new WipeForm(dialog.FileNames).Show(this);
    }

    private void PickFolderToWipe()
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder to securely delete, with everything in it" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            new WipeForm([dialog.SelectedPath]).Show(this);
    }
}
