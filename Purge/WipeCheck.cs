using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Purge;

/// <summary>
/// A reason why overwriting a file may not destroy its old contents.
/// </summary>
/// <param name="Title">Short label, e.g. "Compressed file" or "Solid-state drive C:".</param>
/// <param name="Detail">What the problem is.</param>
/// <param name="AppliesToDrive">
/// True for problems with the drive or folder rather than the file itself, which only
/// need to be reported once.
/// </param>
public sealed record WipeWarning(string Title, string Detail, bool AppliesToDrive)
{
    public override string ToString() => $"{Title} - {Detail}";
}

/// <summary>
/// Spots files whose old contents can survive being overwritten, so Purge can say so
/// instead of implying the data is gone.
/// </summary>
public static class WipeCheck
{
    private static readonly ConcurrentDictionary<string, bool?> SolidStateCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<WipeWarning> For(FileInfo file)
    {
        var warnings = new List<WipeWarning>();
        string path = file.FullName;

        FileAttributes attributes;
        try
        {
            attributes = file.Attributes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return warnings;
        }

        if (attributes.HasFlag(FileAttributes.Compressed))
            warnings.Add(new("Compressed file",
                "NTFS may write the overwritten data to new places on the disk, leaving the old data where it was.", false));
        if (attributes.HasFlag(FileAttributes.SparseFile))
            warnings.Add(new("Sparse file",
                "NTFS may write the overwritten data to new places on the disk, leaving the old data where it was.", false));
        if (attributes.HasFlag(FileAttributes.Encrypted))
            warnings.Add(new("EFS-encrypted file",
                "unencrypted copies from before the file was encrypted may still be on the disk.", false));

        if (IsUnderOneDrive(path))
            warnings.Add(new("OneDrive folder",
                "OneDrive keeps earlier versions and the Recycle Bin online; delete them at onedrive.com as well.", true));

        string? root = Path.GetPathRoot(path);
        if (root is null)
            return warnings;

        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            warnings.Add(new("Network drive",
                "the server decides where data is written and may keep snapshots or previous versions.", true));
            return warnings;
        }

        string letter = root.TrimEnd('\\');
        if (DriveDetails(root) is not { } drive)
            return warnings; // unknown drive details: no drive warnings

        if (drive.Type == DriveType.Network)
            warnings.Add(new($"Network drive {letter}",
                "the server decides where data is written and may keep snapshots or previous versions.", true));
        else if (string.Equals(drive.Format, "ReFS", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new($"ReFS drive {letter}",
                "ReFS writes changes to new places on the disk, so the old data may remain.", true));

        if (drive.Type != DriveType.Network && IsSolidState(letter) == true)
            warnings.Add(new($"Solid-state drive {letter}",
                "SSDs and flash drives write to new memory cells and erase the old ones later, so overwriting " +
                "can't guarantee the old data is gone. Full-disk encryption (BitLocker) is the reliable protection.", true));

        return warnings;
    }

    private static readonly ConcurrentDictionary<string, (DriveType Type, string Format)?> DriveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drive type and file system, looked up once per drive (folders can hold thousands of files).</summary>
    private static (DriveType Type, string Format)? DriveDetails(string root) =>
        DriveCache.GetOrAdd(root, r =>
        {
            try
            {
                var drive = new DriveInfo(r);
                return (drive.DriveType, drive.DriveFormat);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        });

    private static bool IsUnderOneDrive(string path)
    {
        foreach (string variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            string? folder = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(folder))
                continue;
            folder = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True for an SSD or other drive with no seek penalty, false for a spinning disk,
    /// null if Windows won't say (for example, some USB sticks and RAID volumes).
    /// </summary>
    /// <param name="drive">A drive letter such as <c>C:</c>.</param>
    public static bool? IsSolidState(string drive)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return SolidStateCache.GetOrAdd(drive, QuerySolidState);
    }

    private static bool? QuerySolidState(string drive)
    {
        // Access 0 is enough to query device properties, so no admin rights are needed.
        using SafeFileHandle volume = CreateFileW(@"\\.\" + drive, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (volume.IsInvalid)
            return null;

        var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                             out SeekPenaltyDescriptor result, Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, 0))
            return null;

        return result.IncursSeekPenalty == 0;
    }

    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public int Version;
        public int Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security,
                                                     uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize,
                                               out SeekPenaltyDescriptor output, int outputSize, out int returned, nint overlapped);
}
