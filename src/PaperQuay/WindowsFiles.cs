using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaperQuay;

internal static class WindowsFiles
{
    private const uint ReadAccess = 0x80000000;
    private const uint WriteAccess = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;

    public static SafeFileHandle OpenRead(string path, bool forDeletion = false)
    {
        var handle = Open(path, ReadAccess | (forDeletion ? DeleteAccess : 0), FileShare.Read, OpenExisting);
        try
        {
            if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("The saved PDF path does not point to a regular file.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle CreateStaging(string path) =>
        Open(path, WriteAccess | DeleteAccess, FileShare.None, CreateNew);

    // Rename the open staging file itself, with replacement disabled.
    public static void Publish(SafeFileHandle handle, string destination)
    {
        var name = Encoding.Unicode.GetBytes(NativePath(destination));
        var nameOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName)).ToInt32();
        var size = Math.Max(Marshal.SizeOf<RenameInfo>(), nameOffset + name.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new RenameInfo { FileNameLength = (uint)name.Length }, buffer, false);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetRename(handle, FileRenameInfo, buffer, (uint)size))
                throw LastError(destination);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void Delete(SafeFileHandle handle, string path)
    {
        byte delete = 1;
        if (!SetDisposition(handle, FileDispositionInfo, ref delete, sizeof(byte)))
            throw LastError(path);
    }

    private static SafeFileHandle Open(string path, uint access, FileShare sharing, uint disposition)
    {
        var handle = CreateFile(NativePath(path), access, sharing, IntPtr.Zero, disposition, OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = LastError(path);
        handle.Dispose();
        throw error;
    }

    private static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static Exception LastError(string path)
    {
        var error = Marshal.GetLastWin32Error();
        var detail = new Win32Exception(error);
        var message = $"{detail.Message} {path}";
        return error switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message, detail),
            _ => new IOException(message, detail)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfo
    {
        public uint ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRename(SafeFileHandle handle, int kind, IntPtr info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref byte info, uint size);
}
