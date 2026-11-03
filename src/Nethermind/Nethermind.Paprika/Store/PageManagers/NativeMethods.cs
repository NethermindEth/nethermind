// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Paprika.Store.PageManagers;

/// <summary>
/// The native calls of <see cref="MemoryMappedPageManager"/>.
/// </summary>
internal static unsafe class NativeMethods
{
    private const int MADV_RANDOM = 1;
    private const int O_RDONLY = 0;
    private const int EINTR = 4;
    private const int ENOSPC = 28;
    private const int ENOSYS = 38;
    private const int EOPNOTSUPP = 95;
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(byte* address, nuint length, int advice);

    [DllImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static extern int Fallocate(int fd, int mode, long offset, long length);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    /// <summary>
    /// Advises random access over a mapping, so a page fault does not read the neighbouring pages ahead. Linux only.
    /// </summary>
    /// <remarks>
    /// <see cref="FileOptions.RandomAccess"/> advises the file descriptor, which page faults of a mapping do not follow.
    /// The advice is best effort, so a failure is ignored.
    /// </remarks>
    public static void AdviseRandom(byte* address, long length)
    {
        if (OperatingSystem.IsLinux())
        {
            Madvise(address, (nuint)length, MADV_RANDOM);
        }
    }

    /// <summary>
    /// Reserves the disk blocks of <c>[offset, offset + length)</c> of the file. Linux only.
    /// </summary>
    /// <returns><c>false</c> when the file system does not support reserving blocks.</returns>
    /// <exception cref="IOException">The disk is full, or the call failed otherwise.</exception>
    public static bool TryAllocate(SafeFileHandle file, long offset, long length)
    {
        while (Fallocate((int)file.DangerousGetHandle(), 0, offset, length) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            switch (errno)
            {
                case EINTR:
                    continue;
                case EOPNOTSUPP or ENOSYS:
                    return false;
                case ENOSPC:
                    throw new IOException($"No space left on the device to reserve {length} bytes at offset {offset} of the file.");
                default:
                    throw new IOException($"Reserving {length} bytes at offset {offset} of the file failed with errno {errno}.");
            }
        }

        return true;
    }

    /// <summary>
    /// Makes the entries of a directory durable, so that a file created in it survives a crash. Linux and macOS only, as
    /// .NET cannot open a directory.
    /// </summary>
    public static void FsyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var fd = Open(path, O_RDONLY);
        if (fd < 0)
        {
            throw new IOException($"Opening the directory {path} failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                throw new IOException($"Syncing the directory {path} failed with errno {Marshal.GetLastPInvokeError()}.");
            }
        }
        finally
        {
            Close(fd);
        }
    }

    /// <summary>
    /// Marks the file sparse, so that Windows allocates only the ranges written. Windows only.
    /// </summary>
    public static void MarkSparse(SafeFileHandle file)
    {
        if (!DeviceIoControl(file, FSCTL_SET_SPARSE, 0, 0, 0, 0, out _, 0))
        {
            throw new IOException($"Marking the file sparse failed with error {Marshal.GetLastPInvokeError()}.");
        }
    }
}
