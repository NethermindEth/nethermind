// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.Merge.Plugin.KeepAlive;

/// <summary>
/// Reads 4 KiB blocks of a dedicated file from the device, past the page cache, so that the device the file lives
/// on sees a request. Linux only; one thread at a time.
/// </summary>
/// <remarks>
/// The file is opened with <c>O_DIRECT</c>, and each read goes to the device. Where the file system refuses
/// <c>O_DIRECT</c> (at open, or at the first read), the probe falls back to dropping the file from the page cache
/// with <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> before each buffered <c>pread</c>, without read-ahead; the file is
/// clean, so the kernel drops it and the read goes to the device. The file is written once with random bytes and synced, so its blocks
/// are allocated on the device: a hole, or a block a file system can compress away, would be read without a request.
/// </remarks>
internal sealed class DiskReadProbe : IDisposable
{
    public const int BlockSize = 4096;
    public const int DefaultFileSize = 4 * 1024 * 1024;

    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int POSIX_FADV_RANDOM = 1;
    private const int POSIX_FADV_DONTNEED = 4;
    private const int EINTR = 4;
    private const int EINVAL = 22;

    private readonly string _path;
    private readonly long _blocks;
    private nint _allocation;
    private readonly nint _buffer;
    private int _fd = -1;
    private long _next;

    public DiskKeepAliveReadMode Mode { get; private set; }

    public string Path => _path;

    private DiskReadProbe(string path, long blocks)
    {
        _path = path;
        _blocks = blocks;
        // O_DIRECT needs the buffer aligned to the logical block size; a page covers every device.
        _allocation = Marshal.AllocHGlobal(BlockSize * 2);
        _buffer = (_allocation + (BlockSize - 1)) & ~(nint)(BlockSize - 1);
    }

    /// <summary>The <c>O_DIRECT</c> flag of this architecture, or 0 where it is not known.</summary>
    internal static int DirectFlag => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x4000,
        Architecture.Arm64 => 0x10000,
        _ => 0,
    };

    /// <summary>Creates the file when it is missing or of another size, and opens it.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="fileSize">Its size, a multiple of <see cref="BlockSize"/>.</param>
    /// <param name="allowDirect">Whether to try <c>O_DIRECT</c> before the page-cache fallback.</param>
    /// <exception cref="IOException">The file could not be created or opened.</exception>
    public static DiskReadProbe Open(string path, int fileSize, bool allowDirect)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The disk keep-alive needs 64-bit Linux.");
        if (fileSize < BlockSize || fileSize % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize), fileSize, $"Must be a positive multiple of {BlockSize}.");

        EnsureFile(path, fileSize);

        DiskReadProbe probe = new(path, fileSize / BlockSize);
        try
        {
            if (!(allowDirect && DirectFlag != 0 && probe.TryOpen(DiskKeepAliveReadMode.Direct, out _)))
            {
                if (!probe.TryOpen(DiskKeepAliveReadMode.DropCache, out int errno))
                    throw new IOException($"open({path}) failed with errno {errno}.");
            }

            return probe;
        }
        catch
        {
            probe.Dispose();
            throw;
        }
    }

    /// <summary>Reads the next block.</summary>
    /// <returns>How long the <c>pread</c> took, in <see cref="Stopwatch"/> ticks.</returns>
    /// <exception cref="IOException">The read failed.</exception>
    public long Read()
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);

        long offset = (_next++ % _blocks) * BlockSize;
        if (TryRead(offset, out long elapsed, out int errno)) return elapsed;

        // Some file systems accept O_DIRECT at open and refuse it at the read.
        if (Mode == DiskKeepAliveReadMode.Direct && errno == EINVAL)
        {
            CloseFd();
            if (!TryOpen(DiskKeepAliveReadMode.DropCache, out errno))
                throw new IOException($"open({_path}) failed with errno {errno} after O_DIRECT reads were refused.");
            if (TryRead(offset, out elapsed, out errno)) return elapsed;
        }

        throw new IOException($"pread({_path}, {offset}) failed with errno {errno}.");
    }

    private bool TryRead(long offset, out long elapsed, out int errno)
    {
        if (Mode == DiskKeepAliveReadMode.DropCache)
        {
            // The whole file, not the block: the page cache can hold the file in large folios, and the kernel drops
            // only the folios that lie wholly inside the range (on 7.0, one 4 KiB block was never dropped). The file
            // is small and only this probe reads it. Best effort: a block the kernel keeps shows as a fast read.
            PosixFadvise(_fd, 0, 0, POSIX_FADV_DONTNEED);
        }

        nint read;
        long start = Stopwatch.GetTimestamp();
        do
        {
            read = PRead(_fd, _buffer, BlockSize, offset);
        } while (read < 0 && Marshal.GetLastPInvokeError() == EINTR);

        elapsed = Stopwatch.GetTimestamp() - start;
        errno = read < 0 ? Marshal.GetLastPInvokeError() : 0;
        return read == BlockSize;
    }

    private bool TryOpen(DiskKeepAliveReadMode mode, out int errno)
    {
        int flags = O_RDONLY | O_CLOEXEC | (mode == DiskKeepAliveReadMode.Direct ? DirectFlag : 0);
        int fd = OpenFile(_path, flags);
        if (fd < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return false;
        }

        // No read-ahead: it would fill the cache with the next blocks and the reads after would not reach the device.
        if (mode == DiskKeepAliveReadMode.DropCache) PosixFadvise(fd, 0, 0, POSIX_FADV_RANDOM);

        _fd = fd;
        Mode = mode;
        errno = 0;
        return true;
    }

    private static void EnsureFile(string path, int fileSize)
    {
        FileInfo info = new(path);
        if (info.Exists && info.Length == fileSize) return;

        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        byte[] content = new byte[fileSize];
        Random.Shared.NextBytes(content);
        string temporary = path + ".tmp";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private void CloseFd()
    {
        if (_fd >= 0)
        {
            CloseFile(_fd);
            _fd = -1;
        }

        Mode = DiskKeepAliveReadMode.None;
    }

    public void Dispose()
    {
        CloseFd();
        if (_allocation != 0)
        {
            Marshal.FreeHGlobal(_allocation);
            _allocation = 0;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static extern nint PRead(int fd, nint buffer, nuint count, long offset);

    [DllImport("libc", EntryPoint = "posix_fadvise")]
    private static extern int PosixFadvise(int fd, long offset, long length, int advice);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseFile(int fd);
}
