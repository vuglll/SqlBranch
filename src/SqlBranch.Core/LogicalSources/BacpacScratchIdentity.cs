using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SqlBranch.Core.LogicalSources;

/// <summary>Identifies a scratch directory or file independently of its path and contents.</summary>
internal static class BacpacScratchIdentity
{
    public static string Read(FileStream stream) =>
        OperatingSystem.IsWindows() ? ReadWindows(stream.SafeFileHandle) : Read(stream.Name);

    public static string Read(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(path);
        }

        var start = new ProcessStartInfo("/usr/bin/stat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Clear();
        start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c");
        start.ArgumentList.Add("%d:%i");
        start.ArgumentList.Add(path);
        try
        {
            using var stat = Process.Start(start) ?? throw new IOException("Could not inspect BACPAC scratch.");
            var identity = stat.StandardOutput.ReadToEnd().Trim();
            stat.WaitForExit();
            return stat.ExitCode == 0 && identity.Length > 0
                ? identity
                : throw new IOException("Could not identify BACPAC scratch.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException("Could not inspect BACPAC scratch.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindows(string path)
    {
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        using var handle = OpenPath(path, readAttributes, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, openExisting, backupSemantics | openReparsePoint, IntPtr.Zero);
        return ReadWindows(handle);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindows(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException("Could not identify BACPAC scratch.");
        }
        return FormattableString.Invariant($"{info.VolumeSerialNumber}:{info.FileIndexHigh}:{info.FileIndexLow}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

#pragma warning disable SYSLIB1054 // Use safe-handle marshalling without enabling unsafe code.
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenPath(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
#pragma warning restore SYSLIB1054
}
