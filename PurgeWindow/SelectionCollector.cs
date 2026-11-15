using System.Diagnostics;
using System.IO.Pipes;

namespace PurgeWindow;

/// <summary>
/// Gathers a multi-file Explorer selection into one window.
/// </summary>
/// <remarks>
/// For "Securely delete", Explorer starts one PurgeWindow.exe per selected file. The first
/// copy to start becomes the primary: it opens the window and listens on a named pipe.
/// The other copies send their file to the primary and exit. The primary stops listening
/// once no new file has arrived for a short while, so a later, separate right-click
/// opens its own window.
/// </remarks>
public sealed class SelectionCollector : IDisposable
{
    private readonly Semaphore _gate;
    private readonly string _channel;
    private bool _released;

    private SelectionCollector(Semaphore gate, string channel)
    {
        _gate = gate;
        _channel = channel;
    }

    /// <summary>One channel per user and Windows session.</summary>
    public static string DefaultChannel =>
        $"Purge.SecureDelete.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Becomes the primary for <paramref name="channel"/>, or returns null if another
    /// copy already is.
    /// </summary>
    public static SelectionCollector? TryBecomePrimary(string channel)
    {
        // A semaphore rather than a mutex: it is released from whichever thread finishes
        // collecting, and Windows removes it if the process ends.
        var gate = new Semaphore(1, 1, @"Local\" + channel);
        if (gate.WaitOne(0))
            return new SelectionCollector(gate, channel);

        gate.Dispose();
        return null;
    }

    /// <summary>
    /// Sends paths to the primary. Returns false if it can't be reached, in which case
    /// the caller should show its own window.
    /// </summary>
    public static bool TrySend(string channel, IEnumerable<string> paths, int timeoutMs = 3000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", channel, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe);
            foreach (string path in paths)
                writer.WriteLine(path);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Receives paths from other copies, calling <paramref name="onPath"/> for each, until
    /// none has arrived for <paramref name="quiet"/>. Then gives up being the primary.
    /// </summary>
    /// <remarks>
    /// Started from the UI thread, <paramref name="onPath"/> runs on the UI thread.
    /// </remarks>
    public async Task CollectAsync(Action<string> onPath, TimeSpan quiet, CancellationToken cancel = default)
    {
        try
        {
            while (true)
            {
                using var server = new NamedPipeServerStream(_channel, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    wait.CancelAfter(quiet);
                    try
                    {
                        await server.WaitForConnectionAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(cancel) is { } line)
                {
                    if (line.Length > 0)
                        onPath(line);
                }
            }
        }
        finally
        {
            Release();
        }
    }

    private void Release()
    {
        if (_released)
            return;
        _released = true;
        _gate.Release();
    }

    public void Dispose()
    {
        Release();
        _gate.Dispose();
    }
}
