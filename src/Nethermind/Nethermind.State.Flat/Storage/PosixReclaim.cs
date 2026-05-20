// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Storage;

/// <summary>Outcome of a <see cref="PosixReclaim.TryPunchHole"/> attempt.</summary>
internal enum PunchHoleOutcome
{
    /// <summary>The range was hole-punched (or there was nothing to punch).</summary>
    Done,

    /// <summary>The filesystem/kernel permanently does not support hole-punching.</summary>
    Unsupported,

    /// <summary>A transient error — hole-punching may succeed on a later call.</summary>
    Failed,
}

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
    /// without changing the file length. A successful punch also invalidates the OS page
    /// cache for the range, so a follow-up <c>posix_fadvise(DONTNEED)</c> is unnecessary.
    /// </summary>
    /// <returns>
    /// <see cref="PunchHoleOutcome.Done"/> on success (or an empty range);
    /// <see cref="PunchHoleOutcome.Unsupported"/> on non-Linux or a permanent
    /// <c>EOPNOTSUPP</c> / <c>ENOSYS</c>; <see cref="PunchHoleOutcome.Failed"/> on any
    /// other (transient) errno.
    /// </returns>
    internal static PunchHoleOutcome TryPunchHole(int fd, long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return PunchHoleOutcome.Unsupported;
        (long start, long len) = AlignInward(offset, size);
        if (len <= 0) return PunchHoleOutcome.Done;
        if (Fallocate(fd, FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE, start, len) == 0)
            return PunchHoleOutcome.Done;
        int err = Marshal.GetLastPInvokeError();
        return err is EOPNOTSUPP or ENOSYS ? PunchHoleOutcome.Unsupported : PunchHoleOutcome.Failed;
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
