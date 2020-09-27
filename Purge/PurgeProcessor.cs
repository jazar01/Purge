using System;

namespace Purge
{
    public class PurgeProcessor
    {
        /// <summary>
        /// This is the main method for purging files
        /// </summary>
        /// <param name="keepNumber"></param>
        /// <param name="keepDays"></param>
        /// <param name="securityLevel"></param>
        /// <param name="whatIf"></param>
        /// <param name="force"></param>
        /// <param name="prompt"></param>
        /// <param name="fileSpec"></param>
        public PurgeProcessor(int keepNumber, int keepDays, int securityLevel, bool whatIf, bool force, bool prompt, string fileSpec)
        {
            //TODO determine error handling
            FileList fileList = new FileList(fileSpec);           // build the list of files matching
            fileList.MarkFilesForDeletion(keepNumber, keepDays);  // mark the candidates for purging

            int keepCount = 0;
            int deleteCount = 0;

            // loop thru all of the files found in the dir that match the pattern in filespec
            foreach (FileEntry File in fileList.Items)
            {
                if (File.IsCandidate)
                {
                    // the file is a candidate for purging
                    Console.WriteLine("File Age: {0} Purging Filename: {1} ", File.Age, File.File.FullName);
                    if (!whatIf)             //only purge if whatif is false
                    {
                        if (prompt)          // do we need a prompt
                        {
                            Console.Write("   Press 'Y' to Purge or 'N' to skip purging this file: ");
                            char key;
                            do
                            {
                                ConsoleKeyInfo keypress = Console.ReadKey(true);
                                key = keypress.KeyChar;
                            }
                            while (!(key == 'Y' || key == 'y' || key == 'N' || key == 'n'));
                            Console.WriteLine(key);

                            if (key == 'N' || key == 'n')
                            {
                                Console.WriteLine("     Skipping file {0} ", File.File.FullName);
                                keepCount++;  // user answered NO to prompt, keeping the file      
                                continue;
                            }
                        }

                        // purge the file
                        if (force) File.File.IsReadOnly = false;
                        File.wipe(securityLevel);      // TODO handle failure
                        deleteCount++;
                    }
                }
                else
                    // File is not a candidate for purging
                    keepCount++;            
            }

            Console.WriteLine("Files purged: {0}   Files retained: {1}", deleteCount, keepCount);
            if (whatIf)
                Console.WriteLine(" NOTE:  --whatif was set, 0 files were actually purged");

        }
    }
}
