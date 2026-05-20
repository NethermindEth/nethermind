// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Thin fd-based wrappers over the Linux <c>fallocate</c> / <c>posix_fadvise</c> syscalls,
/// used to reclaim disk blocks and OS file-cache pages of dead persisted-snapshot arena
/// ranges. Shared by both the metadata arena (<see cref="ArenaFile"/>, mmap-backed) and the
/// blob arena (<see cref="BlobArenaFile"/>, pread-backed).
/// </summary>
internal static class PosixReclaim
{
    private const int FALLOC_FL_KEEP_SIZE = 0x01;
    private const int FALLOC_FL_PUNCH_HOLE = 0x02;
    private const int POSIX_FADV_DONTNEED = 4;
    // errno values that mean the call will never succeed on this filesystem/kernel.
    private const int ENOSYS = 38;
    private const int EOPNOTSUPP = 95;
    private static readonly long PageSize = Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static extern int Fallocate(int fd, int mode, long offset, long len);

    [DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
    private static extern int PosixFadvise(int fd, long offset, long len, int advice);

    /// <summary>
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> over the page-aligned subrange of
    /// <c>[offset, offset + size)</c>, dropping it from the OS file cache. No-op on
    /// non-Linux; fire-and-forget (the errno is not inspected).
    /// </summary>
    internal static void FadviseDontNeed(int fd, long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;
        (long start, long len) = AlignInward(offset, size);
        if (len <= 0) return;
        PosixFadvise(fd, start, len, POSIX_FADV_DONTNEED);
    }

    /// <summary>
    /// <c>fallocate(FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE)</c> over the page-aligned
    /// subrange of <c>[offset, offset + size)</c>, freeing the underlying disk blocks
    /// without changing the file length.
    /// </summary>
    /// <returns>
    /// <c>true</c> if punch-hole is (still) usable on this descriptor's filesystem;
    /// <c>false</c> on non-Linux or when the kernel reports the operation permanently
    /// unsupported (<c>EOPNOTSUPP</c> / <c>ENOSYS</c>), so the caller can stop trying.
    /// A transient failure (any other errno) still returns <c>true</c>.
    /// </returns>
    internal static bool TryPunchHole(int fd, long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return false;
        (long start, long len) = AlignInward(offset, size);
        if (len <= 0) return true;
        if (Fallocate(fd, FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE, start, len) == 0)
            return true;
        int err = Marshal.GetLastPInvokeError();
        return err is not (EOPNOTSUPP or ENOSYS);
    }

    // Round offset up and end down to OS-page boundaries so only fully-covered pages are
    // touched — mirrors ArenaFile.AdviseDontNeed's rounding and keeps a hole punch from
    // zeroing a partial page shared with a neighbouring reservation.
    private static (long start, long len) AlignInward(long offset, long size)
    {
        long start = (offset + PageSize - 1) & ~(PageSize - 1);
        long end = (offset + size) & ~(PageSize - 1);
        return (start, end - start);
    }
}
