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
    /// bytes, truncates it, renames it to a random name, and deletes it.
    /// With 0 passes the file is simply deleted.
    /// </summary>
    /// <remarks>
    /// Overwriting is best effort: SSDs (wear levelling), copy-on-write or compressed
    /// file systems, and shadow copies may keep the old data elsewhere on the disk.
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
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                                           bufferSize: 1, FileOptions.WriteThrough))
            {
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
}
