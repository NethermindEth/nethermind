// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Thin fd-based wrappers over the Linux <c>fallocate</c> / <c>posix_fadvise</c> syscalls,
/// used to reclaim disk blocks and OS file-cache pages of dead persisted-snapshot arena
/// ranges. Shared by both the metadata arena (<see cref="ArenaFile"/>, mmap-backed) and the
/// blob arena (<see cref="BlobArenaFile"/>, pread-backed).
/// </summary>
internal static class PosixReclaim
{
    internal enum PunchHoleOutcome
    {
        /// <summary>The range was hole-punched (or there was nothing to punch).</summary>
        Done,

        /// <summary>The filesystem/kernel permanently does not support hole-punching.</summary>
        Unsupported,

        /// <summary>A transient error — hole-punching may succeed on a later call.</summary>
        Failed,
    }

    private const int FALLOC_FL_KEEP_SIZE = 0x01;
    private const int FALLOC_FL_PUNCH_HOLE = 0x02;
    private const int POSIX_FADV_DONTNEED = 4;
    private const int POSIX_FADV_WILLNEED = 3;
    // errno values that mean the call will never succeed on this filesystem/kernel.
    private const int ENOSYS = 38;
    private const int EOPNOTSUPP = 95;
    private static readonly long PageSize = Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static extern int Fallocate(int fd, int mode, long offset, long len);

    [DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
    private static extern int PosixFadvise(int fd, long offset, long len, int advice);

    [DllImport("libc", EntryPoint = "fdatasync", SetLastError = true)]
    private static extern int FdatasyncSyscall(int fd);

    /// <summary>
    /// <c>fdatasync(2)</c> on <paramref name="fd"/> — block until every byte previously
    /// written has reached durable storage. Skips the mtime/ctime metadata flush that
    /// <c>fsync(2)</c> would do but still flushes the file size (required for future reads
    /// of the auto-grown blob file). No-op on non-Linux (test environments only —
    /// durability matters on the production Linux target). Throws <see cref="IOException"/>
    /// on errno.
    /// </summary>
    internal static void Fsync(int fd)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (FdatasyncSyscall(fd) == 0) return;
        int err = Marshal.GetLastPInvokeError();
        throw new IOException($"fdatasync failed: errno {err}");
    }

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
    /// <c>posix_fadvise(POSIX_FADV_WILLNEED)</c> over <c>[offset, offset + size)</c>, asking
    /// the kernel to start asynchronous read-ahead for the range. No-op on non-Linux;
    /// fire-and-forget (the errno is not inspected).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="FadviseDontNeed"/> the range is passed unaligned: <c>WILLNEED</c>
    /// must <em>cover</em> the whole region (including the partial pages at either end), and
    /// the kernel page-aligns the request internally. Inward alignment would shave the first
    /// and last page — a base snapshot's region boundaries are not page-aligned.
    /// </remarks>
    internal static void FadviseWillNeed(int fd, long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (size <= 0) return;
        PosixFadvise(fd, offset, size, POSIX_FADV_WILLNEED);
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
    // touched — prevents a hole punch from zeroing a partial page shared with a neighbouring reservation.
    private static (long start, long len) AlignInward(long offset, long size)
    {
        long start = (offset + PageSize - 1) & ~(PageSize - 1);
        long end = (offset + size) & ~(PageSize - 1);
        return (start, end - start);
    }
}
