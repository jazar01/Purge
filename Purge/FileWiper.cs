using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Purge;

/// <summary>
/// Deletes files, optionally overwriting their contents with random bytes first.
/// </summary>
public static class FileWiper
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Overwrites the file <paramref name="passes"/> times with cryptographically random
    /// bytes (including any NTFS alternate data streams), truncates it, renames it to a
    /// random name, and deletes it. With 0 passes the file is simply deleted.
    /// </summary>
    /// <remarks>
    /// Overwriting is best effort: see <see cref="WipeCheck"/> for the cases where the old
    /// data can survive (SSDs, compressed or copy-on-write file systems, shadow copies, ...).
    /// </remarks>
    public static void WipeAndDelete(FileInfo file, int passes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(passes);

        file.Refresh();
        if (!file.Exists)
            throw new FileNotFoundException("File not found.", file.FullName);

        string path = file.FullName;

        if (passes > 0)
        {
            Overwrite(file, passes);

            // Obscure the original file name before deleting it.
            string renamed = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName());
            try
            {
                System.IO.File.Move(path, renamed);
                path = renamed;
            }
            catch (IOException)
            {
                // Renaming is a nicety; still delete under the original name.
            }
        }

        System.IO.File.Delete(path);
    }

    /// <summary>
    /// Overwrites every data stream of the file (the main one and any alternate data
    /// streams) <paramref name="passes"/> times and truncates them to zero length.
    /// </summary>
    internal static void Overwrite(FileInfo file, int passes)
    {
        // Alternate data streams can hold real content (some apps store data or
        // thumbnails there), and deleting the file would just free their clusters.
        foreach (string stream in AlternateStreams(file.FullName))
            OverwriteStream(file.FullName + stream, passes);

        OverwriteStream(file.FullName, passes);
    }

    private static void OverwriteStream(string path, int passes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                                      bufferSize: 1, FileOptions.WriteThrough);
        long length = fs.Length;
        byte[] buffer = new byte[(int)Math.Clamp(length, 1, BufferSize)];

        for (int pass = 0; pass < passes; pass++)
        {
            fs.Position = 0;
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                fs.Write(buffer, 0, count);
                remaining -= count;
            }
            fs.Flush(flushToDisk: true);
        }

        fs.SetLength(0);
    }

    /// <summary>
    /// Names of the file's alternate data streams, such as <c>:Zone.Identifier:$DATA</c>.
    /// Empty when there are none, or on file systems without them.
    /// </summary>
    internal static List<string> AlternateStreams(string path)
    {
        var streams = new List<string>();
        if (!OperatingSystem.IsWindows())
            return streams;

        nint handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == -1)
            return streams; // no streams, or the file system doesn't support them

        try
        {
            do
            {
                // The main stream is "::$DATA"; the others are ":name:$DATA".
                if (data.StreamName != "::$DATA")
                    streams.Add(data.StreamName);
            }
            while (FindNextStreamW(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }
        return streams;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260 + 36)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstStreamW(string fileName, int infoLevel, out Win32FindStreamData data, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(nint handle, out Win32FindStreamData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
}
