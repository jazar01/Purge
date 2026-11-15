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

    private SelectionCollector? _collector;
    private readonly System.Windows.Forms.Timer _quiet;
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
    private bool _showHint;
    private CancellationTokenSource? _stop;
    private bool _closeWhenStopped;

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
        _list.Columns.Add("Name", 170);
        _list.Columns.Add("Location", 170);
        _list.Columns.Add("Size", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 270);
        _list.ItemActivate += (_, _) =>
        {
            if (_list.FocusedItem?.Tag is string path)
                ShowInExplorer(path);
        };

        _warnings = Ui.Label("", maxWidth: 690);
        _warnings.ForeColor = Color.FromArgb(150, 80, 0);
        _warnings.Visible = false;

        _passes = Ui.Number(1, 50, width: 55);
        _passes.ValueChanged += (_, _) => UpdateState();
        _force = new CheckBox { Text = "Also delete read-only files", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(24, 6, 3, 3) };
        var options = Ui.Row(Ui.Label("Overwrite passes:"), _passes, Ui.Label("(0 = delete without overwriting)"), _force);

        _status = Ui.Label("");
        _delete = Ui.Button("Delete", async (_, _) => await DeleteAsync());
        // While deleting, this button is "Stop".
        _close = Ui.Button("Cancel", (_, _) =>
        {
            if (_busy)
                AskToStop();
            else
                Close();
        });
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

        // Delete waits until no more items have arrived for a moment.
        _quiet = new System.Windows.Forms.Timer { Interval = (int)GatherQuietTime.TotalMilliseconds };
        _quiet.Tick += (_, _) =>
        {
            _quiet.Stop();
            _gathering = false;
            UpdateState();
        };

        _gathering = collector is not null;
        UpdateState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_collector is not null)
            Collect(_collector);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _quiet.Dispose();
        _collector?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Adds the items other copies of PurgeWindow send (the rest of a multi-item selection,
    /// or a later right-click) until deleting starts.
    /// </summary>
    private async void Collect(SelectionCollector collector)
    {
        Arrived();
        try
        {
            await foreach (string path in collector.Paths.ReadAllAsync())
            {
                AddPath(path);
                Arrived();
            }
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
        }
    }

    private void Arrived()
    {
        _gathering = true;
        _quiet.Stop();
        _quiet.Start();
        UpdateState();
    }

    /// <summary>
    /// Stops taking items from other copies, so the list is final. Returns false if items
    /// arrived at the last moment; collecting then resumes so they can be checked first.
    /// </summary>
    private bool StopCollecting()
    {
        if (_collector is not { } collector)
            return true;

        collector.StopListening();
        int before = _list.Items.Count;
        while (collector.Paths.TryRead(out string? late))
            AddPath(late);
        if (_list.Items.Count == before)
            return true;

        ResumeCollecting();
        return false;
    }

    /// <summary>Starts taking items from other copies again (after "No" to deleting).</summary>
    private void ResumeCollecting()
    {
        if (_collector is not { } old || old.IsListening)
            return;
        _collector = SelectionCollector.TryBecomePrimary(old.Channel);
        old.Dispose();
        if (_collector is not null)
            Collect(_collector);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            // Closing while deleting means stopping first; the window closes once it has stopped.
            e.Cancel = true;
            if (_stop?.IsCancellationRequested == true || AskToStop())
                _closeWhenStopped = true;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Asks whether to stop deleting, and stops if the answer is yes.</summary>
    /// <returns>True if deleting is stopping.</returns>
    private bool AskToStop()
    {
        if (_stop is null || _stop.IsCancellationRequested)
            return _stop is not null;

        var answer = MessageBox.Show(this,
            "Stop deleting?\n\n" +
            "Whatever has already been deleted can't be brought back. A file being overwritten right now is left " +
            "partly overwritten, and anything not reached yet is left untouched.",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes || !_busy)
            return false;

        Stop();
        return true;
    }

    /// <summary>Stops deleting without asking (the Stop button asks first).</summary>
    public void Stop()
    {
        if (_stop is null || _stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        _close.Enabled = false;
        _close.Text = "Stopping…";
        _status.Text = "Stopping…";
    }

    /// <summary>True while deleting (for tests).</summary>
    public bool IsDeleting => _busy;

    /// <summary>The overwrite passes chosen in the window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Passes
    {
        get => (int)_passes.Value;
        set => _passes.Value = value;
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

    /// <summary>Deletes everything listed without asking first (for tests).</summary>
    public Task DeleteWithoutConfirmingAsync() => DeleteAsync(confirm: false);

    private async Task DeleteAsync(bool confirm = true)
    {
        // The list must be final before asking: items still arriving go to a new window.
        if (!StopCollecting())
            return; // more arrived at the last moment; they're listed now, so check again

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
        if (confirm)
        {
            var answer = MessageBox.Show(this,
                $"Permanently delete {what}, {ByteSize.Format(total)}?\n\n{how} " +
                "Nothing goes to the Recycle Bin, and it cannot be recovered.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                ResumeCollecting();
                return;
            }
        }

        _busy = true;
        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        _passes.Enabled = _force.Enabled = _delete.Enabled = false;
        _close.Text = "Stop";

        int deleted = 0, failed = 0;
        try
        {
            foreach (var item in items)
            {
                item.EnsureVisible();
                string path = item.Tag is FolderItem f ? f.Path : ((FileInfo)item.Tag!).FullName;

                if (stop.IsCancellationRequested)
                {
                    LeftBehind(item, path, "Not deleted: stopped before reaching it");
                    failed++;
                    continue;
                }

                try
                {
                    if (item.Tag is FileInfo file)
                        (deleted, failed) = Add((deleted, failed), await DeleteFileAsync(item, file, passes, force, stop));
                    else if (item.Tag is FolderItem folder)
                        (deleted, failed) = Add((deleted, failed), await DeleteFolderAsync(item, folder.Contents!, passes, force, stop));
                }
                catch (Exception e)
                {
                    // Anything unexpected is shown against the item; the rest still goes ahead.
                    LeftBehind(item, path, "Failed: " + e.Message);
                    failed++;
                }

                if (item.Tag is FileInfo or FolderItem)
                    item.Tag = null; // done; a path left here means "show this in File Explorer"
                if (!stop.IsCancellationRequested)
                    _status.Text = $"Deleted {Plural(deleted, "file")}…";
            }
        }
        finally
        {
            bool stopped = stop.IsCancellationRequested;
            _busy = false;
            _stop.Dispose();
            _stop = null;
            _close.Enabled = true;
            _close.Text = "Close";
            _status.Text = (stopped ? "Stopped. " : "") +
                           $"Deleted {Plural(deleted, "file")}." +
                           (failed > 0 ? $" {Plural(failed, "item")} {(stopped ? "not" : "could not be")} deleted." : "") +
                           (_showHint ? " Double-click an item to show it in File Explorer." : "");
            _list.AutoResizeColumn(3, ColumnHeaderAutoResizeStyle.ColumnContent);
            _status.ForeColor = failed > 0 ? Color.Firebrick : SystemColors.ControlText;
        }

        if (_closeWhenStopped)
            Close();
    }

    private static (int, int) Add((int Deleted, int Failed) a, (int Deleted, int Failed) b) =>
        (a.Deleted + b.Deleted, a.Failed + b.Failed);

    /// <summary>Overwrites and deletes one selected file. Returns (files deleted, items left).</summary>
    private async Task<(int, int)> DeleteFileAsync(ListViewItem item, FileInfo file, int passes, bool force, CancellationToken stop)
    {
        string action = passes > 0 ? "Overwriting…" : "Deleting…";
        item.SubItems[3].Text = action;

        long work = file.Length * passes, written = 0;
        bool finished = false;
        var ui = new Progress<long>(n =>
        {
            if (!finished)
                item.SubItems[3].Text = action + Percent(n, work);
        });
        var progress = Throttle(ui, n => written = n);

        try
        {
            var result = await Task.Run(() => SecureDelete.Delete(file.FullName, passes, force, progress, stop));
            if (result.Deleted)
            {
                Done(item, "Deleted");
                return (1, 0);
            }
            LeftBehind(item, file.FullName, "Failed: " + Explain(result.Error!));
            return (0, 1);
        }
        catch (OperationCanceledException)
        {
            LeftBehind(item, file.FullName, written > 0
                ? Sentence(SecureDelete.StoppedWhileOverwriting)
                : "Not deleted: stopped before reaching it");
            return (0, 1);
        }
        finally
        {
            finished = true;
        }
    }

    /// <summary>Securely deletes one selected folder. Returns (files deleted, items left).</summary>
    private async Task<(int, int)> DeleteFolderAsync(ListViewItem item, FolderContents contents, int passes, bool force, CancellationToken stop)
    {
        long work = contents.TotalSize * passes;

        // Progress reports are posted to the window and can arrive after the delete
        // has finished; ignore those so they don't overwrite the final status.
        bool finished = false;
        var progress = new Progress<WipeProgress>(p =>
        {
            if (!finished && !stop.IsCancellationRequested)
                item.SubItems[3].Text = $"Deleting {Math.Min(p.FilesDone + 1, contents.Files.Count)} of {contents.Files.Count}…" + Percent(p.BytesWritten, work);
        });
        item.SubItems[3].Text = $"Deleting 1 of {contents.Files.Count}…";
        var result = await Task.Run(() => FolderWipe.Delete(contents, passes, force, progress, stop));
        finished = true;

        if (result.FolderRemoved && result.Failures.Count == 0)
        {
            Done(item, "Deleted");
            return (result.FilesDeleted, 0);
        }

        var inside = result.Failures.Where(f => !SamePath(f.Path, contents.Path)).ToList();
        var self = result.Failures.FirstOrDefault(f => SamePath(f.Path, contents.Path));

        Failed(item, result.Stopped
            ? $"Stopped: {Plural(inside.Count, "item")} left (listed below)"
            : result.FolderRemoved
                ? $"Deleted, with {Plural(inside.Count, "problem")} (listed below)"
                : inside.Count == 0
                    ? $"Not removed: {self?.Error}"
                    : $"Partly deleted: {Plural(inside.Count, "item")} left (listed below)");
        if (!result.FolderRemoved)
        {
            item.Tag = contents.Path; // double-click shows it in File Explorer
            item.ToolTipText = $"{contents.Path}\n{(inside.Count == 0 ? self?.Error : FolderWipe.KeptMessage)}";
        }
        ShowFailures(item, inside);
        _showHint = true;
        return (result.FilesDeleted, Math.Max(inside.Count, result.FolderRemoved ? 0 : 1));
    }

    /// <summary>Marks an item as left behind, with why; double-clicking it shows it in File Explorer.</summary>
    private void LeftBehind(ListViewItem item, string path, string status)
    {
        Failed(item, status);
        item.Tag = path;
        item.ToolTipText = $"{path}\n{status}";
        _showHint = true;
    }

    /// <summary>" 37%" of the work done, or "" when there's nothing to measure.</summary>
    private static string Percent(long done, long work) =>
        work > 0 ? $" {Math.Min(99, done * 100 / work)}%" : "";

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Passes every value to <paramref name="always"/> (on the calling thread) but only one every
    /// <see cref="FolderWipe.ProgressInterval"/> on to <paramref name="ui"/>, which posts to the window.
    /// </summary>
    private static IProgress<long> Throttle(IProgress<long> ui, Action<long> always)
    {
        var since = System.Diagnostics.Stopwatch.StartNew();
        return new Callback<long>(n =>
        {
            always(n);
            if (since.Elapsed >= FolderWipe.ProgressInterval)
            {
                since.Restart();
                ui.Report(n);
            }
        });
    }

    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
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

    /// <summary>
    /// Lists what was left behind in a folder, with the reason, as indented rows under it.
    /// Double-clicking a row shows that item in File Explorer.
    /// </summary>
    private void ShowFailures(ListViewItem folderItem, IReadOnlyList<DeleteResult> failures)
    {
        int index = folderItem.Index + 1;
        foreach (var failure in failures.Take(MaxFailureRows))
        {
            string reason = Explain(failure.Error!);
            var row = new ListViewItem("    ↳ " + Path.GetFileName(failure.Path))
            {
                ForeColor = Color.Firebrick,
                ToolTipText = $"{failure.Path}\n{reason}",
                Tag = failure.Path,
            };
            row.SubItems.Add(Path.GetDirectoryName(failure.Path) ?? "");
            row.SubItems.Add(File.Exists(failure.Path) ? ByteSize.Format(new FileInfo(failure.Path).Length) : "");
            row.SubItems.Add(reason);
            _list.Items.Insert(index++, row);
        }

        if (failures.Count > MaxFailureRows)
        {
            _list.Items.Insert(index, new ListViewItem($"    … and {failures.Count - MaxFailureRows} more (double-click the folder to see them)")
            {
                ForeColor = Color.Firebrick,
                Tag = folderItem.Tag as string,
            });
        }
    }

    /// <summary>Opens File Explorer with the item selected (or its folder, if it's gone).</summary>
    private static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{parent}\"");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer couldn't be started; nothing more to do.
        }
    }

    /// <summary>The paths of the rows listing what was left behind (for tests).</summary>
    public IReadOnlyList<(string Path, string Reason)> LeftoverRows =>
        _list.Items.Cast<ListViewItem>()
             .Where(i => i.Tag is string && i.Text.StartsWith("    ↳ ", StringComparison.Ordinal))
             .Select(i => ((string)i.Tag!, i.SubItems[3].Text))
             .ToList();

    /// <summary>The Status text of the row for a selected path (for tests).</summary>
    public string StatusOf(string path) =>
        _list.Items.Cast<ListViewItem>().First(i => string.Equals(i.ToolTipText.Split('\n')[0], path, StringComparison.OrdinalIgnoreCase)).SubItems[3].Text;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Summary(int files, int folders) =>
        (files, folders) switch
        {
            (_, 0) => Plural(files, "file"),
            (0, _) => Plural(folders, "folder"),
            _ => $"{Plural(files, "file")} and {Plural(folders, "folder")}",
        };

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
