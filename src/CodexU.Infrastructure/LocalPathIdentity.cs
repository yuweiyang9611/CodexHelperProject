using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexU.Infrastructure;

internal static class LocalPathIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static string CanonicalDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"目录不存在，无法验证路径身份：{fullPath}");
        }

        if (OperatingSystem.IsWindows())
        {
            return CanonicalWindowsDirectoryPath(fullPath);
        }

        if (OperatingSystem.IsLinux())
        {
            return CanonicalUnixPath(fullPath);
        }

        // ResolveLinkTarget covers the common final-component symlink case on other
        // platforms. Windows and Linux, the supported desktop targets, use native
        // handle/realpath identity above and also resolve linked ancestors.
        var info = new DirectoryInfo(fullPath);
        return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
    }

    private static string CanonicalWindowsDirectoryPath(string path)
    {
        using var handle = CreateFileW(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"无法打开目录以验证路径身份：{path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var builder = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandleW(
            handle,
            builder,
            (uint)builder.Capacity,
            flags: 0);
        if (length == 0 || length >= (uint)builder.Capacity)
        {
            throw new IOException(
                $"无法解析目录的最终路径：{path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return NormalizeWindowsDevicePath(builder.ToString());
    }

    private static string CanonicalUnixPath(string path)
    {
        var pointer = RealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException(
                $"无法解析目录的最终路径：{path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                ?? throw new IOException($"无法解析目录的最终路径：{path}");
        }
        finally
        {
            Free(pointer);
        }
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
