#Advanced file deletion

Utility to delete files with some advanced features.

This utility was initially designed to delete backup files that are no longer needed.
It is designed as a command line utility to be run as part of a script or scheduled
task. There is no reason why it wouldn't be good for managing other files that grow
in numbers over time, like log files for example.

If you have a backup scheduled on a certain frequency, say "Daily".  Lets also say the
backup file is zipped and has a name that includes the date, something like this:
"Finance Backup September 25, 2020 04:00.zip".    These files accumulate on a network or
external drive until deleted.  The standard delete commands usually have a parameter 
that allows for deleting files older than a certain date or even a number of days old. 
 

But what if you want to make sure you have at least 5 previous versions on hand at all 
times, in addition to a number of days.  For example; I want to keep 30 days worth of
daily backups, but under no circumstances do I want to have less than 5 backups.  Lets 
say your backup job starts failing and you didn't realize it.  After 30 days, all of your
backups would be deleted. 

The Purge utility provides the functionality to specify a minimum number of backups in 
addition to a number of days to retain.  It will only delete files in a specified directory 
and filename pattern, if they meet the age AND number criteria.

The Purge utility also has the option to securely wipe files as they are deleted by 
overwriting the file with random bytes.  This is useful for wiping sensitive log files
or backup files.

Future consideration:  Send an alert via email if no new files are found for x days.
   While this sounds like a good idea, it might be better off in a separate utility.
   
##<This is a work in progress.  A working version should be checked in around October 2020.>


#Usage:
  Purge [options] \<FileSpec\>
              |  
#Arguments:   |
  \<FileSpec\>|                             Filespec is the full path and pattern for the files
              |                            to be targeted for deletion.  Wildcards are allowed
              |                            in the filename but not in the directory portion of
              |                            the patch.

#Options:     |
  -n, --keep-number <keep-number>        | Keep at least this number of files. [default: 0]
  -d, --keep-days <keep-days>            | Keep files that are newer than this number of days. [default: 0]
  -s, --security-level <security-level>  | Number of passes of security overwriting. [default: 0]
  -t, --whatif                           | Test mode, does not actually purge any files.
  -f, --force                            | Force deletion of read-only files.
  -p, --prompt                           | Prompt for confirmatino before deleting each file.
  --version                              | Show version information
  -?, -h, --help                         | Show help and usage information
