using Purge;
using ByteSize = Purge.Size;
using Size = System.Drawing.Size;

namespace PurgeWindow;

/// <summary>
/// "Securely delete…": overwrite and delete the files and folders selected in Explorer.
/// </summary>
public sealed class WipeForm : Form
{
    private static readonly TimeSpan GatherQuietTime = TimeSpan.FromSeconds(1.5);
    private const int MaxFailureRows = 50;

    /// <summary>A selected folder; <see cref="Contents"/> is null until it has been scanned.</summary>
    private sealed class FolderItem(string path)
    {
        public string Path { get; } = path;
        public FolderContents? Contents { get; set; }
        public IReadOnlyList<WipeWarning> Warnings { get; set; } = [];
    }

    private readonly SelectionCollector? _collector;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListView _list;
    private readonly NumericUpDown _passes;
    private readonly CheckBox _force;
    private readonly Label _status;
    private readonly Label _warnings;
    private readonly Dictionary<string, IReadOnlyList<WipeWarning>> _fileWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _delete;
    private readonly Button _close;
    private readonly List<Task> _scans = [];
    private bool _gathering;
    private int _scanning;
    private bool _busy;

    /// <param name="paths">The files and folders to delete.</param>
    /// <param name="collector">
    /// When given, more paths arriving from other copies of PurgeWindow (a multi-item
    /// Explorer selection) are added to the list.
    /// </param>
    public WipeForm(IEnumerable<string> paths, SelectionCollector? collector = null)
    {
        _collector = collector;

        Text = "Securely delete - Purge";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 440);
        MinimumSize = new Size(520, 320);
        ShowIcon = false;

        var intro = Ui.Label(
            "These files will be overwritten with random data and then deleted, and the folders removed with " +
            "everything in them. Nothing goes to the Recycle Bin, and it cannot be recovered.", maxWidth: 690);

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Location", 230);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 180);

        _warnings = Ui.Label("", maxWidth: 690);
        _warnings.ForeColor = Color.FromArgb(150, 80, 0);
        _warnings.Visible = false;

        _passes = Ui.Number(1, 50, width: 55);
        _passes.ValueChanged += (_, _) => UpdateState();
        _force = new CheckBox { Text = "Also delete read-only files", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(24, 6, 3, 3) };
        var options = Ui.Row(Ui.Label("Overwrite passes:"), _passes, Ui.Label("(0 = delete without overwriting)"), _force);

        _status = Ui.Label("");
        _delete = Ui.Button("Delete", async (_, _) => await DeleteAsync());
        _close = Ui.Button("Cancel", (_, _) => Close());
        CancelButton = _close;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro);
        layout.Controls.Add(_list);
        layout.Controls.Add(_warnings);
        layout.Controls.Add(options);
        layout.Controls.Add(Ui.ButtonBar(_status, _delete, _close));
        Controls.Add(layout);

        // The status label sits at the left of the button bar.
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        foreach (string path in paths)
            AddPath(path);

        _gathering = collector is not null;
        UpdateState();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_collector is null)
            return;

        try
        {
            await _collector.CollectAsync(AddPath, GatherQuietTime);
        }
        finally
        {
            _gathering = false;
            UpdateState();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Please wait until the deleting is finished.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        base.OnFormClosing(e);
    }

    /// <summary>Files listed and ready to delete.</summary>
    public IReadOnlyList<string> PendingFiles =>
        PendingItems.Select(i => i.Tag).OfType<FileInfo>().Select(f => f.FullName).ToList();

    /// <summary>Folders listed and ready to delete.</summary>
    public IReadOnlyList<string> PendingFolders =>
        PendingItems.Select(i => i.Tag).OfType<FolderItem>().Select(f => f.Path).ToList();

    /// <summary>Completes when every listed folder has been scanned (for tests).</summary>
    public Task ScansFinished => Task.WhenAll(_scans);

    private void AddPath(string path)
    {
        if (!_seen.Add(path))
            return;

        var item = new ListViewItem(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path);
        item.SubItems.Add(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "");
        item.SubItems.Add("");
        item.SubItems.Add("");
        item.ToolTipText = path;
        _list.Items.Add(item);

        if (Directory.Exists(path))
        {
            if (FolderWipe.WhyNotAllowed(path) is { } reason)
            {
                item.SubItems[3].Text = "Not allowed: " + reason;
                item.ForeColor = Color.Firebrick;
            }
            else
            {
                var folder = new FolderItem(path);
                item.Tag = folder;
                item.SubItems[3].Text = "Counting…";
                _scans.Add(ScanAsync(item, folder));
            }
        }
        else if (new FileInfo(path) is { Exists: true } file)
        {
            item.SubItems[2].Text = ByteSize.Format(file.Length);
            item.SubItems[3].Text = file.IsReadOnly ? "Read-only" : "";
            item.Tag = file;
            _fileWarnings[file.FullName] = WipeCheck.For(file);
        }
        else
        {
            item.SubItems[3].Text = "Not found";
            item.ForeColor = SystemColors.GrayText;
        }

        UpdateState();
    }

    private async Task ScanAsync(ListViewItem item, FolderItem folder)
    {
        _scanning++;
        UpdateState();
        try
        {
            var (contents, warnings) = await Task.Run(() =>
            {
                var c = FolderWipe.Scan(folder.Path);
                var w = c.Files.SelectMany(WipeCheck.For).ToList();
                return (c, w);
            });
            folder.Contents = contents;
            folder.Warnings = warnings;
            item.SubItems[2].Text = ByteSize.Format(contents.TotalSize);
            item.SubItems[3].Text = Describe(contents);
            if (contents.Unreadable.Count > 0)
                item.ForeColor = Color.Firebrick;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            item.Tag = null;
            item.SubItems[3].Text = "Can't read: " + e.Message;
            item.ForeColor = Color.Firebrick;
        }
        finally
        {
            _scanning--;
            UpdateState();
        }
    }

    private static string Describe(FolderContents c)
    {
        if (c.Folders.Count == 0)
            return "Folder link - only the link is removed";
        if (c.Files.Count == 0 && c.SubfolderCount == 0 && c.Links.Count == 0 && c.Unreadable.Count == 0)
            return "Empty folder";
        string text = $"Folder: {Plural(c.Files.Count, "file")}";
        if (c.SubfolderCount > 0)
            text += $", {Plural(c.SubfolderCount, "subfolder")}";
        if (c.Unreadable.Count > 0)
            text += $" ({c.Unreadable.Count} unreadable)";
        return text;
    }

    private IEnumerable<ListViewItem> PendingItems =>
        _list.Items.Cast<ListViewItem>().Where(i => i.Tag is FileInfo or FolderItem);

    private void UpdateState()
    {
        var pending = PendingItems.ToList();
        var files = pending.Select(i => i.Tag).OfType<FileInfo>().ToList();
        var folders = pending.Select(i => i.Tag).OfType<FolderItem>().ToList();
        long total = files.Sum(f => f.Length) + folders.Sum(f => f.Contents?.TotalSize ?? 0);

        _delete.Enabled = !_gathering && _scanning == 0 && !_busy && pending.Count > 0;
        if (_busy)
            return;

        _status.Text = _gathering ? "Gathering the selected items…"
            : _scanning > 0 ? "Counting the files in the folders…"
            : pending.Count == 0 ? "Nothing to delete."
            : $"{Summary(files.Count, folders.Count)}, {ByteSize.Format(total)}";

        _warnings.Text = WarningText;
        _warnings.Visible = _warnings.Text.Length > 0;
    }

    /// <summary>
    /// Why overwriting may not destroy the listed files' old contents, or "" if there is
    /// no known reason (or the files won't be overwritten at all).
    /// </summary>
    public string WarningText
    {
        get
        {
            if (_passes.Value == 0)
                return "";

            var warnings = PendingItems.SelectMany(i => i.Tag switch
            {
                FileInfo f => _fileWarnings.GetValueOrDefault(f.FullName, []),
                FolderItem folder => folder.Warnings,
                _ => [],
            });

            var lines = warnings
                .GroupBy(w => w.Title)
                .Select(g => g.First().AppliesToDrive || g.Count() == 1
                    ? $"⚠  {g.Key} - {g.First().Detail}"
                    : $"⚠  {g.Key} ({g.Count()} files) - {g.First().Detail}")
                .ToList();
            return string.Join("\n", lines);
        }
    }

    private async Task DeleteAsync()
    {
        var items = PendingItems.ToList();
        var folders = items.Select(i => i.Tag).OfType<FolderItem>().ToList();
        int fileCount = items.Count(i => i.Tag is FileInfo);
        int filesInFolders = folders.Sum(f => f.Contents!.Files.Count);
        long total = items.Sum(i => i.Tag switch
        {
            FileInfo f => f.Length,
            FolderItem f => f.Contents!.TotalSize,
            _ => 0,
        });
        int passes = (int)_passes.Value;
        bool force = _force.Checked;

        string what = folders.Count == 0
            ? Plural(fileCount, "file")
            : $"{Summary(fileCount, folders.Count)} ({Plural(fileCount + filesInFolders, "file")} in all)";
        string how = passes == 0
            ? "Files will be deleted without being overwritten."
            : $"Every file will be overwritten {Plural(passes, "time")} and then deleted.";
        var answer = MessageBox.Show(this,
            $"Permanently delete {what}, {ByteSize.Format(total)}?\n\n{how} " +
            "Nothing goes to the Recycle Bin, and it cannot be recovered.",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _busy = true;
        _passes.Enabled = _force.Enabled = _close.Enabled = _delete.Enabled = false;

        int deleted = 0, failed = 0;
        foreach (var item in items)
        {
            item.EnsureVisible();

            if (item.Tag is FileInfo file)
            {
                item.SubItems[3].Text = passes > 0 ? "Overwriting…" : "Deleting…";
                var result = await Task.Run(() => SecureDelete.Delete(file.FullName, passes, force));
                if (result.Deleted)
                {
                    Done(item, "Deleted");
                    deleted++;
                }
                else
                {
                    Failed(item, "Failed: " + Explain(result.Error!));
                    failed++;
                }
            }
            else if (item.Tag is FolderItem folder)
            {
                var contents = folder.Contents!;
                var progress = new Progress<int>(n => item.SubItems[3].Text = $"Deleting {n} of {contents.Files.Count}…");
                var result = await Task.Run(() => FolderWipe.Delete(contents, passes, force, progress));
                deleted += result.FilesDeleted;

                if (result.FolderRemoved && result.Failures.Count == 0)
                {
                    Done(item, "Deleted");
                }
                else
                {
                    Failed(item, result.FolderRemoved
                        ? $"Deleted, with {Plural(result.Failures.Count, "problem")}"
                        : $"Partly deleted: {Plural(result.Failures.Count, "item")} left");
                    failed += result.Failures.Count(f => !string.Equals(f.Path, contents.Path, StringComparison.OrdinalIgnoreCase));
                    ShowFailures(item, contents.Path, result.Failures);
                }
            }

            item.Tag = null; // done, either way
            _status.Text = $"Deleted {Plural(deleted, "file")}…";
        }

        _busy = false;
        _close.Enabled = true;
        _close.Text = "Close";
        _status.Text = $"Deleted {Plural(deleted, "file")}." + (failed > 0 ? $" {Plural(failed, "item")} could not be deleted." : "");
        _status.ForeColor = failed > 0 ? Color.Firebrick : SystemColors.ControlText;
    }

    private static void Done(ListViewItem item, string status)
    {
        item.SubItems[3].Text = status;
        item.ForeColor = SystemColors.GrayText;
    }

    private static void Failed(ListViewItem item, string status)
    {
        item.SubItems[3].Text = status;
        item.ForeColor = Color.Firebrick;
    }

    private static string Explain(string error) =>
        error == "read-only" ? "read-only (tick the box to include)" : error;

    /// <summary>Lists what was left behind in a folder, as indented rows under it.</summary>
    private void ShowFailures(ListViewItem folderItem, string folderPath, IReadOnlyList<DeleteResult> failures)
    {
        int index = folderItem.Index + 1;
        foreach (var failure in failures.Where(f => !string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)).Take(MaxFailureRows))
        {
            var row = new ListViewItem("    ↳ " + Path.GetFileName(failure.Path))
            {
                ForeColor = Color.Firebrick,
                ToolTipText = failure.Path,
            };
            row.SubItems.Add(Path.GetDirectoryName(failure.Path) ?? "");
            row.SubItems.Add("");
            row.SubItems.Add(Explain(failure.Error!));
            _list.Items.Insert(index++, row);
        }

        int more = failures.Count(f => !string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)) - MaxFailureRows;
        if (more > 0)
            _list.Items.Insert(index, new ListViewItem($"    … and {more} more") { ForeColor = Color.Firebrick });
    }

    private static string Summary(int files, int folders) =>
        (files, folders) switch
        {
            (_, 0) => Plural(files, "file"),
            (0, _) => Plural(folders, "folder"),
            _ => $"{Plural(files, "file")} and {Plural(folders, "folder")}",
        };

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
