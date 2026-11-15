using System.Text;

namespace PurgeWindow;

/// <summary>Small helpers for building the windows in code.</summary>
internal static class Ui
{
    public static Label Label(string text, int maxWidth = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 3, 6),
        MaximumSize = new Size(maxWidth, 0),
    };

    public static Button Button(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(90, 28),
            Margin = new Padding(6, 3, 0, 3),
        };
        button.Click += onClick;
        return button;
    }

    public static NumericUpDown Number(int value, int max, int width = 70) => new()
    {
        Minimum = 0,
        Maximum = max,
        Value = value,
        Width = width,
        Anchor = AnchorStyles.Left,
        TextAlign = HorizontalAlignment.Right,
    };

    /// <summary>A row of controls laid out left to right.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left,
        };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>A right-aligned row of buttons for the bottom of a window.</summary>
    public static FlowLayoutPanel ButtonBar(params Control[] buttons)
    {
        var bar = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        bar.Controls.AddRange(buttons.Reverse().ToArray());
        return bar;
    }
}

/// <summary>
/// A TextWriter that appends each completed line to a control on the UI thread, so a
/// purge running in the background shows its progress as it goes.
/// </summary>
internal sealed class UiWriter(Control target, Action<string> appendLine) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value != '\n')
        {
            _line.Append(value);
            return;
        }

        string text = _line.ToString().TrimEnd('\r');
        _line.Clear();
        target.BeginInvoke((Action)(() => appendLine(text)));
    }
}
