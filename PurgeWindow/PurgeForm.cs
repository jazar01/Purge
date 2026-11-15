using Purge;
using ByteSize = Purge.Size;
using Size = System.Drawing.Size;

namespace PurgeWindow;

/// <summary>
/// "Purge files…": purge files in a folder by pattern and keep rules, like Purge.exe.
/// Purge stays disabled until a preview with the current settings has been shown.
/// </summary>
public sealed class PurgeForm : Form
{
    private readonly TextBox _folder;
    private readonly TextBox _pattern;
    private readonly NumericUpDown _keepNumber;
    private readonly NumericUpDown _keepDays;
    private readonly ComboBox _ageBy;
    private readonly TextBox _minSize;
    private readonly TextBox _maxSize;
    private readonly CheckBox _recurse;
    private readonly CheckBox _removeEmpty;
    private readonly NumericUpDown _passes;
    private readonly CheckBox _force;
    private readonly TextBox _output;
    private readonly Label _status;
    private readonly Button _preview;
    private readonly Button _purge;
    private readonly Button _close;
    private int _previewCount;
    private bool _busy;

    public PurgeForm(string? folder = null)
    {
        Text = "Purge files - Purge";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 620);
        MinimumSize = new Size(600, 520);
        ShowIcon = false;

        _folder = new TextBox { Text = folder ?? "", Width = 400, Anchor = AnchorStyles.Left };
        var browse = Ui.Button("Browse…", (_, _) => Browse());
        _pattern = new TextBox { Width = 200, PlaceholderText = "e.g. *.bak or Backup*.zip", Anchor = AnchorStyles.Left };
        _keepNumber = Ui.Number(0, 100_000);
        _keepDays = Ui.Number(0, 100_000);
        _ageBy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Anchor = AnchorStyles.Left };
        _ageBy.Items.AddRange(["Modified time", "Created time"]);
        _ageBy.SelectedIndex = 0;
        _minSize = new TextBox { Width = 90, PlaceholderText = "no minimum" };
        _maxSize = new TextBox { Width = 90, PlaceholderText = "no maximum" };
        _recurse = new CheckBox { Text = "Include subfolders (each folder keeps its own newest files)", AutoSize = true, Anchor = AnchorStyles.Left };
        _removeEmpty = new CheckBox
        {
            Text = "Remove subfolders the purge leaves empty",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(23, 0, 3, 3), // indented under "Include subfolders"
            Enabled = false,
        };
        _passes = Ui.Number(1, 50, width: 55);
        _force = new CheckBox { Text = "Also delete read-only files", AutoSize = true, Anchor = AnchorStyles.Left };

        var settings = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(string label, Control control)
        {
            settings.Controls.Add(Ui.Label(label));
            settings.Controls.Add(control);
        }
        AddRow("Folder:", Ui.Row(_folder, browse));
        AddRow("Files matching:", _pattern);
        AddRow("Always keep:", Ui.Row(_keepNumber, Ui.Label("newest files")));
        AddRow("Also keep files newer than:", Ui.Row(_keepDays, Ui.Label("days, measured from"), _ageBy));
        AddRow("Only files sized:", Ui.Row(_minSize, Ui.Label("to"), _maxSize, Ui.Label("e.g. 500KB, 1.5MB, 2GB")));
        AddRow("", _recurse);
        AddRow("", _removeEmpty);
        AddRow("Overwrite passes:", Ui.Row(_passes, Ui.Label("before deleting (0 = just delete)")));
        AddRow("", _force);

        _output = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9F),
            BackColor = SystemColors.Window,
        };

        _status = Ui.Label("");
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _preview = Ui.Button("Preview", async (_, _) => await RunAsync(whatIf: true));
        _purge = Ui.Button("Purge…", async (_, _) => await RunAsync(whatIf: false));
        _close = Ui.Button("Close", (_, _) => Close());
        CancelButton = _close;
        AcceptButton = _preview;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(settings);
        layout.Controls.Add(Ui.Label("Click Preview to see which files would be purged. Nothing is deleted until you click Purge and confirm."));
        layout.Controls.Add(_output);
        layout.Controls.Add(Ui.ButtonBar(_status, _preview, _purge, _close));
        Controls.Add(layout);

        // Any change to the settings means the preview no longer applies.
        foreach (Control c in new Control[] { _folder, _pattern, _minSize, _maxSize })
            c.TextChanged += (_, _) => PreviewOutOfDate();
        foreach (var n in new[] { _keepNumber, _keepDays, _passes })
            n.ValueChanged += (_, _) => PreviewOutOfDate();
        _ageBy.SelectedIndexChanged += (_, _) => PreviewOutOfDate();
        _recurse.CheckedChanged += (_, _) =>
        {
            _removeEmpty.Enabled = _recurse.Checked;
            PreviewOutOfDate();
        };
        _removeEmpty.CheckedChanged += (_, _) => PreviewOutOfDate();
        _force.CheckedChanged += (_, _) => PreviewOutOfDate();

        PreviewOutOfDate();
        ActiveControl = _pattern;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Please wait until the purge finishes.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        base.OnFormClosing(e);
    }

    /// <summary>Settings for tests and for the window itself.</summary>
    public void SetPattern(string pattern) => _pattern.Text = pattern;

    /// <summary>Ticks "Include subfolders" and "Remove subfolders the purge leaves empty" (for tests).</summary>
    public void SetSubfolders(bool recurse, bool removeEmpty)
    {
        _recurse.Checked = recurse;
        _removeEmpty.Checked = removeEmpty;
    }

    private void PreviewOutOfDate()
    {
        if (_busy)
            return;
        _previewCount = 0;
        _purge.Enabled = false;
        _status.Text = "";
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Folder to purge" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _folder.Text = dialog.SelectedPath;
    }

    /// <summary>Builds the options from the window, or returns an error message.</summary>
    public (PurgeOptions? Options, string? Error) BuildOptions(bool whatIf)
    {
        string folder = _folder.Text.Trim().Trim('"');
        string pattern = _pattern.Text.Trim();

        if (folder.Length == 0)
            return (null, "Choose a folder.");
        if (!Directory.Exists(folder))
            return (null, $"Folder not found: {folder}");
        if (pattern.Length == 0)
            return (null, "Enter which files to consider, for example *.bak.");
        if (pattern.IndexOfAny(['\\', '/', ':']) >= 0)
            return (null, "Enter only a file name pattern (like *.bak), not a folder.");

        long? min = null, max = null;
        if (_minSize.Text.Trim() is { Length: > 0 } minText)
        {
            if (!ByteSize.TryParse(minText, out long value))
                return (null, $"'{minText}' is not a valid size. Use a number with an optional unit: B, KB, MB, GB or TB.");
            min = value;
        }
        if (_maxSize.Text.Trim() is { Length: > 0 } maxText)
        {
            if (!ByteSize.TryParse(maxText, out long value))
                return (null, $"'{maxText}' is not a valid size. Use a number with an optional unit: B, KB, MB, GB or TB.");
            max = value;
        }
        if (min > max)
            return (null, "The minimum size is larger than the maximum size.");

        var options = new PurgeOptions(
            FileSpec: Path.Combine(folder, pattern),
            KeepNumber: (int)_keepNumber.Value,
            KeepDays: (int)_keepDays.Value,
            SecurityLevel: (int)_passes.Value,
            WhatIf: whatIf,
            Force: _force.Checked,
            Recurse: _recurse.Checked,
            RemoveEmptyFolders: _recurse.Checked && _removeEmpty.Checked,
            Selection: new FileSelection(_ageBy.SelectedIndex == 1 ? AgeBy.Created : AgeBy.Modified, min, max));
        return (options, null);
    }

    private async Task RunAsync(bool whatIf)
    {
        var (options, error) = BuildOptions(whatIf);
        if (options is null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!whatIf)
        {
            string how = options.SecurityLevel == 0
                ? "They will be deleted"
                : $"They will be overwritten {options.SecurityLevel} time{(options.SecurityLevel == 1 ? "" : "s")} and deleted";
            var answer = MessageBox.Show(this,
                $"Purge the {_previewCount} file{(_previewCount == 1 ? "" : "s")} shown in the preview?\n\n" +
                $"{how}, not moved to the Recycle Bin, and cannot be recovered.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _busy = true;
        _preview.Enabled = _purge.Enabled = _close.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = whatIf ? "Looking for files…" : "Purging…";
        _output.Clear();

        var writer = new UiWriter(this, line => _output.AppendText(line + Environment.NewLine));
        var processor = new PurgeProcessor(writer, writer);
        int code = await Task.Run(() => processor.Run(options));

        _busy = false;
        _preview.Enabled = _close.Enabled = true;

        if (whatIf)
        {
            _previewCount = processor.Purged;
            _purge.Enabled = code != ExitCodes.Error && _previewCount > 0;
            _status.Text = code == ExitCodes.Error ? "Preview failed."
                : _previewCount == 0 ? "Nothing to purge with these settings."
                : $"{_previewCount} file{(_previewCount == 1 ? "" : "s")} would be purged.";
        }
        else
        {
            _previewCount = 0;
            _status.Text = $"Purged {processor.Purged} file{(processor.Purged == 1 ? "" : "s")}." +
                           (processor.Failed > 0 ? $" {processor.Failed} failed." : "");
        }
        _status.ForeColor = code == ExitCodes.Success ? SystemColors.ControlText : Color.Firebrick;
    }
}
