using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Purge
{
    class FileList
    {
        private FileInfo[] fileInfoList;  // list of raw FileInfo's 
        public List<FileEntry> Items { get; set; }

        /// <summary>
        /// Constructor 
        /// </summary>
        /// <param name="fileSpec"></param>
        public FileList(string fileSpec)
        {
            string directory = Path.GetDirectoryName(fileSpec);
            string fileName = Path.GetFileName(fileSpec);

            if (string.IsNullOrWhiteSpace(directory))
                directory = Environment.CurrentDirectory;

            try  // TODO - Change this to let the exceptions bubble up
                 //  I don't want console I/O coming from an class like this
            {
                DirectoryInfo di = new DirectoryInfo(directory);
                fileInfoList = di.GetFiles(fileName, SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                throw (new DirectoryNotFoundException(directory));
            }
            catch (Exception)
            {
                throw (new FileNotFoundException(fileSpec));
            }

            GetFileList();
        }

        /// <summary>
        /// builds the Items list from the FileInfoList
        /// </summary>
        private void GetFileList()
        {
            Items = new List<FileEntry>();
            if (fileInfoList.Length > 0)
            {
                foreach (FileInfo fi in fileInfoList)
                {
                    FileEntry fileEntry = new FileEntry();

                    fileEntry.Directory = fi.Directory;
                    fileEntry.File = fi;
                    DateTime fileDate = fi.CreationTimeUtc;
                    var Age = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays;
                    fileEntry.Age = (int)Age;

                    Items.Add(fileEntry);
                }

                Items.Sort((x, y) => x.Age - y.Age);  // sort Items by age
            }
        }

        /// <summary>
        /// Mark files in fileList that are candidates for purging
        /// </summary>
        /// <param name="fileList"></param>
        /// <param name="keepNumber"></param>
        /// <param name="keepDays"></param>
        public void MarkFilesForDeletion(int keepNumber, int keepDays)
        {
            /* *****************************************
             *  mark any files  older than keepDays    *
             * *****************************************/
            if (Items.Count > 0)
            {
                foreach (FileEntry file in Items)
                {
                    if (file.Age > keepDays)
                        file.IsCandidate = true;
                    else
                        file.IsCandidate = false;
                }

                /* *****************************************
                 *  make sure keepNumber files will remain *
                 *  ****************************************/

                if (keepCount < keepNumber)
                    UnMarkFiles(keepNumber - keepCount);
            }
        }

        /// <summary>
        /// Count the number of files that are not marked as candidates for purging
        /// </summary>
        /// <param name="fileList"></param>
        /// <returns></returns>
        private int keepCount
        {
            get
            {
                int counter = 0;
                if (Items.Count > 0)
                    foreach (FileEntry file in Items)
                        if (!file.IsCandidate)
                            counter++;

                return counter;
            }
        }

        /// <summary>
        /// return a string representation
        /// </summary>
        public override string ToString()
        {
            StringBuilder result = new StringBuilder();
            if (Items.Count > 0)
                foreach (FileEntry file in Items)
                    result.Append(string.Format("{0}\t{1}\t{2}\n", file.Age, file.IsCandidate, file.File.FullName));

            result.Append(string.Format("Total count:{0}  To be Deleted:{1}   To be Kept:{2}\n", Items.Count, Items.Count - keepCount, keepCount));
            return result.ToString();
        }

        /// <summary>
        /// unmark some of the oldest files 
        ///   this is used when too many files are marked because of Age
        ///   and we need to keep some of them to satisfy the keepNumber
        /// </summary>
        /// <param name="count"></param>
        private void UnMarkFiles(int count)
        {
            if (Items.Count > 0 && count > 0) // make sure there is something valid to do
                foreach (FileEntry file in Items)  // Items are already sorted by age
                    if (file.IsCandidate)
                    {
                        file.IsCandidate = false;
                        count--;
                        if (count == 0)
                            return;
                    }
        }
    }
}
