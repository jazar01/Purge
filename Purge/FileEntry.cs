using System;
using System.IO;

namespace Purge
{
    public class FileEntry
    {
        public DirectoryInfo Directory { get; set; }
        public FileInfo File { get; set; }
        public int Age { get; set; }
        public bool IsCandidate { get; set; }

        /// <summary>
        /// Wipes a file by overwriting it with random bytes
        /// </summary>
        /// <param name="passes"></param>
        public void wipe(int passes)
        {
            // TODO consider a progress indicator
            const int BUFFER_SIZE = 65536;     
            if (!File.Exists)
                throw new FileNotFoundException();

            if (passes > 0)
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                Random rnd = new Random();
                FileStream fs = File.OpenWrite();

                while (passes-- > 0)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    long remainingBytes = File.Length;
                    do
                    {
                        rnd.NextBytes(buffer);
                        int BytesToWrite = (int)Math.Min(BUFFER_SIZE, remainingBytes);
                        fs.Write(buffer, 0, BytesToWrite);  // TODO Handle exceptions
                        remainingBytes -= BytesToWrite;
                        fs.Flush(true);
                    }
                    while (remainingBytes > 0);
                }
                fs.Dispose();
            }
            File.Delete();

        }
    }
}
