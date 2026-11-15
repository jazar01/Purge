using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

namespace PurgeWindow;

/// <summary>
/// Gathers a multi-item Explorer selection into one window.
/// </summary>
/// <remarks>
/// <para>
/// For "Securely delete", Explorer starts one PurgeWindow.exe per selected item. The first
/// copy becomes the primary: it starts listening on a named pipe straight away, before it
/// opens its window or touches any disk, and keeps listening until deleting starts. The
/// other copies send it their items and exit.
/// </para>
/// <para>
/// Nothing depends on how quickly the copies start: the primary acknowledges each hand-off
/// only once it has taken the items, and a copy whose hand-off isn't acknowledged keeps
/// trying, or becomes the primary itself if the old one has stopped listening (for example
/// because it is already deleting). Only if no window can be reached at all for
/// <see cref="GiveUpAfter"/> does a copy open a window of its own.
/// </para>
/// </remarks>
public sealed class SelectionCollector : IDisposable
{
    /// <summary>How long a copy keeps trying to reach the window before opening its own.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private const byte Ack = 1;

    private readonly Semaphore _gate;
    private readonly Channel<string> _paths = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private bool _stopped;

    private SelectionCollector(Semaphore gate, string channel)
    {
        _gate = gate;
        Channel = channel;
        _ = Task.Run(ListenAsync);
    }

    /// <summary>The pipe and semaphore name this collector uses.</summary>
    public string Channel { get; }

    /// <summary>One channel per user and Windows session.</summary>
    public static string DefaultChannel =>
        $"Purge.SecureDelete.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>Paths received from other copies; completes when listening stops.</summary>
    public ChannelReader<string> Paths => _paths.Reader;

    /// <summary>True while other copies' items are being accepted.</summary>
    public bool IsListening
    {
        get { lock (_lock) return !_stopped; }
    }

    /// <summary>
    /// Becomes the primary for <paramref name="channel"/> and starts listening, or returns
    /// null if another copy already is the primary.
    /// </summary>
    public static SelectionCollector? TryBecomePrimary(string channel)
    {
        // A semaphore rather than a mutex: it is released from whichever thread stops
        // listening, and Windows removes it when the last copy holding it ends.
        var gate = new Semaphore(1, 1, @"Local\" + channel);
        if (gate.WaitOne(0))
            return new SelectionCollector(gate, channel);

        gate.Dispose();
        return null;
    }

    /// <summary>
    /// Sends paths to the primary. Returns true only if the primary confirmed it has taken
    /// them; false if it couldn't be reached or has stopped listening.
    /// </summary>
    public static bool TrySend(string channel, IEnumerable<string> paths, int timeoutMs = 2000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", channel, PipeDirection.InOut,
                                                       PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);

            using (var writer = new StreamWriter(pipe, leaveOpen: true) { NewLine = "\n" })
            {
                foreach (string path in paths)
                    writer.WriteLine(path);
                writer.WriteLine(); // an empty line ends the list
            }

            using var timeout = new CancellationTokenSource(timeoutMs);
            var answer = new byte[1];
            int read = pipe.ReadAsync(answer, timeout.Token).AsTask().GetAwaiter().GetResult();
            return read == 1 && answer[0] == Ack;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a newly started copy should do with its <paramref name="paths"/>: hand them to the
    /// window that is already collecting (returns HandedOff), or become the primary and show
    /// the window (returns its collector). Returns neither only if no window could be reached
    /// within <paramref name="giveUpAfter"/>; the copy should then show a window of its own.
    /// </summary>
    public static (bool HandedOff, SelectionCollector? Collector) Join(string channel, IReadOnlyCollection<string> paths, TimeSpan giveUpAfter)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (TryBecomePrimary(channel) is { } collector)
                return (false, collector);
            if (TrySend(channel, paths))
                return (true, null);
            if (waited.Elapsed >= giveUpAfter)
                return (false, null);
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Stops accepting items and gives up being the primary, so a later right-click opens a
    /// new window. Copies sending at that moment get no acknowledgement and try again.
    /// </summary>
    public void StopListening()
    {
        lock (_lock)
        {
            if (_stopped)
                return;
            _stopped = true;
        }
        _stop.Cancel();
        _paths.Writer.TryComplete();
        _gate.Release();
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Channel, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (e is OperationCanceledException)
                    return;
                continue;
            }

            // Serve this copy while the next one connects to a fresh pipe instance.
            _ = ReceiveAsync(server);
        }
    }

    private async Task ReceiveAsync(NamedPipeServerStream server)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var received = new List<string>();
                using (var reader = new StreamReader(server, leaveOpen: true))
                {
                    while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { Length: > 0 } line)
                        received.Add(line);
                }

                lock (_lock)
                {
                    if (_stopped)
                        return; // no acknowledgement: the sender will try again elsewhere
                    foreach (string path in received)
                        _paths.Writer.TryWrite(path);
                }

                await server.WriteAsync(new[] { Ack }, timeout.Token).ConfigureAwait(false);
                await server.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The sender went away or was too slow; if it didn't get its acknowledgement it
                // sends again, and the window ignores items it already has.
            }
        }
    }

    public void Dispose()
    {
        StopListening();
        _stop.Dispose();
        _gate.Dispose();
    }
}
