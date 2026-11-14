using System.Text;

namespace Purge;

/// <summary>
/// A log file that Purge appends timestamped lines to.
/// </summary>
public sealed class LogFile : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    private LogFile(StreamWriter writer, string fullPath, Func<DateTime> clock)
    {
        _writer = writer;
        _clock = clock;
        FullPath = fullPath;
    }

    public string FullPath { get; }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it (and its folder) if needed.
    /// Other programs can read the log while Purge is running.
    /// </summary>
    public static LogFile Open(string path, Func<DateTime>? clock = null)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            throw new IOException($"'{fullPath}' is a folder, not a file.");

        string? folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        // Flush every line so the log is complete even if the process is killed.
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        return new LogFile(writer, fullPath, clock ?? (() => DateTime.Now));
    }

    /// <summary>Writes one timestamped line, optionally tagged (e.g. "ERROR").</summary>
    public void WriteLine(string text, string? tag = null)
    {
        string stamp = _clock().ToString("yyyy-MM-dd HH:mm:ss");
        lock (_lock)
            _writer.WriteLine(tag is null ? $"{stamp}  {text}" : $"{stamp}  {tag}  {text}");
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// Writes to a console stream and copies each completed line to a <see cref="LogFile"/>.
/// </summary>
public sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly LogFile _log;
    private readonly string? _tag;
    private readonly StringBuilder _line = new();

    public TeeWriter(TextWriter console, LogFile log, string? tag = null)
    {
        _console = console;
        _log = log;
        _tag = tag;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value)
    {
        _console.Write(value);
        Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        _console.Write(value);
        foreach (char c in value)
            Append(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        WriteLine();
    }

    public override void WriteLine()
    {
        _console.WriteLine();
        EndLine();
    }

    public override void Flush() => _console.Flush();

    private void Append(char c)
    {
        if (c == '\n')
            EndLine();
        else if (c != '\r')
            _line.Append(c);
    }

    private void EndLine()
    {
        _log.WriteLine(_line.ToString(), _tag);
        _line.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        // Keep anything written without a final newline.
        if (disposing && _line.Length > 0)
            EndLine();
        base.Dispose(disposing);
    }
}
