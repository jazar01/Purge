# Purge
Advanced file deletion

Utility to delete files with some advanced features.

This utility was initially designed to delete backup files that are no longer needed.
It is designed as a command line utility to be run as part of a script or scheduled
task.  

If you have a backup scheduled on a certain frequency, say "Daily".  Lets also say the
backup file is zipped and has a name that includes the date, something like this:
"Finance Backup September 25, 2020 04:00".    These files accumulate on a network or
external drive until deleted.  The standard delete command usually have some a 
parameter that allows for deleting files older than a certain date or even a number of
days old.  

But what if you want to make sure you have at least 10 previous versions on hand at all 
times, in addition to a number of days.  For example; I want to keep 30 days worth of
daily backups, but under no circumstances to I want to have less than 5 backups.  Lets 
say your backup job starts failing and you didn't realize it.  After 30 days, all of your
backups would be deleted. 

The Purge utility provides the functionality to specify a minimum number of backups in 
addition to a number of days to retain.  It will only delete files in a specified directory 
and filename patter, if they meet the age AND number criteria.

The Purge utility also gives the option to securely wipe files as they are deleted by 
overwriting the file with random bytes.

Future consideration:  Send an alert via email if no new files are found for x days.

<This is a work in progress.  A working version should be checked in around October 2020.>
