using Purge;
using ByteSize = Purge.Size;
using Size = System.Drawing.Size;

namespace PurgeWindow;

/// <summary>
/// "Securely delete…": overwrite and delete the files selected in Explorer.
/// </summary>
public sealed class WipeForm : Form
{
    private static readonly TimeSpan GatherQuietTime = TimeSpan.FromSeconds(1.5);

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
    private bool _gathering;
    private bool _busy;

    /// <param name="paths">The files to delete.</param>
    /// <param name="collector">
    /// When given, more files arriving from other copies of PurgeWindow (a multi-file
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
            "These files will be overwritten with random data and then deleted. They do not go to " +
            "the Recycle Bin and cannot be recovered.", maxWidth: 690);

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Folder", 260);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 140);

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
            AddFile(path);

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
            await _collector.CollectAsync(AddFile, GatherQuietTime);
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
            MessageBox.Show(this, "Please wait until the files are deleted.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        base.OnFormClosing(e);
    }

    /// <summary>Files listed and ready to delete.</summary>
    public IReadOnlyList<string> PendingFiles =>
        _list.Items.Cast<ListViewItem>().Where(i => i.Tag is FileInfo).Select(i => ((FileInfo)i.Tag!).FullName).ToList();

    private void AddFile(string path)
    {
        if (!_seen.Add(path))
            return;

        var item = new ListViewItem(Path.GetFileName(path.TrimEnd('\\')));
        item.SubItems.Add(Path.GetDirectoryName(path) ?? "");

        if (Directory.Exists(path))
        {
            item.SubItems.Add("");
            item.SubItems.Add("Folder - skipped");
            item.ForeColor = SystemColors.GrayText;
        }
        else if (new FileInfo(path) is { Exists: true } file)
        {
            item.SubItems.Add(ByteSize.Format(file.Length));
            item.SubItems.Add(file.IsReadOnly ? "Read-only" : "");
            item.Tag = file;
            _fileWarnings[file.FullName] = WipeCheck.For(file);
        }
        else
        {
            item.SubItems.Add("");
            item.SubItems.Add("Not found");
            item.ForeColor = SystemColors.GrayText;
        }

        _list.Items.Add(item);
        UpdateState();
    }

    private IEnumerable<ListViewItem> PendingItems => _list.Items.Cast<ListViewItem>().Where(i => i.Tag is FileInfo);

    private void UpdateState()
    {
        var pending = PendingItems.ToList();
        long total = pending.Sum(i => ((FileInfo)i.Tag!).Length);

        _delete.Enabled = !_gathering && !_busy && pending.Count > 0;
        _status.Text = _gathering
            ? "Gathering the selected files…"
            : pending.Count == 0 ? "No files to delete." : $"{Plural(pending.Count, "file")}, {ByteSize.Format(total)}";

        if (!_busy)
        {
            _warnings.Text = WarningText;
            _warnings.Visible = _warnings.Text.Length > 0;
        }
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

            var lines = PendingItems
                .SelectMany(i => _fileWarnings.GetValueOrDefault(((FileInfo)i.Tag!).FullName, []))
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
        int passes = (int)_passes.Value;
        bool force = _force.Checked;
        long total = items.Sum(i => ((FileInfo)i.Tag!).Length);

        string how = passes == 0
            ? "They will be deleted without being overwritten."
            : $"They will be overwritten {Plural(passes, "time")} and then deleted.";
        var answer = MessageBox.Show(this,
            $"Permanently delete {Plural(items.Count, "file")} ({ByteSize.Format(total)})?\n\n{how} " +
            "They do not go to the Recycle Bin and cannot be recovered.",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _busy = true;
        _passes.Enabled = _force.Enabled = _close.Enabled = false;
        UpdateState();

        int deleted = 0, failed = 0;
        foreach (var item in items)
        {
            var file = (FileInfo)item.Tag!;
            item.SubItems[3].Text = passes > 0 ? "Overwriting…" : "Deleting…";
            item.EnsureVisible();

            var result = await Task.Run(() => SecureDelete.Delete(file.FullName, passes, force));

            item.Tag = null; // done, either way
            if (result.Deleted)
            {
                item.SubItems[3].Text = "Deleted";
                item.ForeColor = SystemColors.GrayText;
                deleted++;
            }
            else
            {
                item.SubItems[3].Text = "Failed: " + result.Error + (result.Error == "read-only" ? " (tick the box to include)" : "");
                item.ForeColor = Color.Firebrick;
                failed++;
            }
            _status.Text = $"Deleted {deleted} of {items.Count}…";
        }

        _busy = false;
        _close.Enabled = true;
        _close.Text = "Close";
        _delete.Enabled = false;
        _status.Text = $"Deleted {Plural(deleted, "file")}." + (failed > 0 ? $" {failed} failed." : "");
        _status.ForeColor = failed > 0 ? Color.Firebrick : SystemColors.ControlText;
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
