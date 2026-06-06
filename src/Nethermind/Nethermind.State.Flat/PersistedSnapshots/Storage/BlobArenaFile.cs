// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A blob arena file storing trie-node RLP bytes. Owns its <see cref="SafeFileHandle"/>
/// and is refcounted: the owning <see cref="BlobArenaManager"/>'s array slot holds the
/// initial lease (count 1), the issuing <see cref="BlobArenaWriter"/> and every leased
/// <see cref="PersistedSnapshots.PersistedSnapshot"/> hold additional ones. The on-disk
/// file is deleted by <see cref="CleanUp"/> when the last lease is released, unless
/// <see cref="PersistOnShutdown"/> was called first — in which case the file is preserved
/// for the next session.
///
/// <para>
/// Reads use a read-only mmap for zero-copy access (see <see cref="GetSpan"/>); writes go
/// through a <see cref="FileStream"/> seeked to the target offset. The file is pre-extended
/// to <see cref="MaxSize"/> (sparse on Linux) so the mapping is fixed for the file's whole
/// life — appends land in already-mapped pages, so <see cref="BasePtr"/> never has to be
/// remapped and stays valid for concurrent readers. The owning manager bounds the resident
/// working set with a <see cref="PageResidencyTracker"/>, exactly as the metadata
/// <see cref="ArenaFile"/> does.
/// </para>
///
/// <para>
/// The first <see cref="HeaderSize"/> bytes hold the file's <see cref="Frontier"/> (int64
/// little-endian); the RLP data region begins at <see cref="HeaderSize"/>. The header is the
/// authority for frontier restoration across restart (the on-disk length is always
/// <see cref="MaxSize"/> after pre-extension, so it can no longer carry the frontier). It is
/// written by <see cref="WriteFrontierHeader"/> and made durable by the caller's
/// <see cref="Fsync"/>.
/// </para>
///
/// <para>
/// Owns its own contribution to <see cref="Metrics.BlobFileCountByTier"/> /
/// <see cref="Metrics.BlobAllocatedBytesByTier"/> under <see cref="Tier"/>: count +1 on
/// construction (plus the restored <see cref="Frontier"/> as allocated bytes); symmetric
/// -1 / -<see cref="ReportedFrontier"/> on <see cref="CleanUp"/>.
/// <see cref="BlobArenaManager.OnWriteCompleted"/> pushes frontier deltas as writes advance.
/// Bytes are reported as **allocated** (Frontier-based), not the pre-extended sparse
/// <see cref="MaxSize"/>.
/// </para>
/// </summary>
public sealed unsafe class BlobArenaFile : RefCountingDisposable
{
    /// <summary>Bytes reserved at the file head for the int64 little-endian frontier value.</summary>
    public const int HeaderSize = 8;

    private const int MADV_RANDOM = 1;
    private const int MADV_DONTNEED = 4;
    private const int MADV_POPULATE_READ = 22;
    private static readonly nuint OsPageSize = (nuint)Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    // Treated as bool; 0 = delete on CleanUp, 1 = keep the on-disk file. Set by
    // PersistOnShutdown via Interlocked.Exchange so it is safe to call from any path.
    private int _preserveOnDispose;

    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private byte* _basePtr;

    internal PersistedSnapshotTier Tier { get; }

    /// <summary>Stable file id, narrowed from int to ushort. Embedded in every <see cref="NodeRef"/>.</summary>
    public ushort BlobArenaId { get; }

    /// <summary>On-disk path. Deleted by <see cref="CleanUp"/> unless <see cref="PersistOnShutdown"/> opted in.</summary>
    private string Path { get; }

    /// <summary>Mapped file size (sparse on Linux). Writers append within this cap; the mmap covers it whole.</summary>
    public long MaxSize { get; }

    /// <summary>Underlying read/write file handle. Used internally by the mmap, header writes and <see cref="OpenWriteStream"/>.</summary>
    private SafeFileHandle Handle { get; }

    /// <summary>Raw pointer to the first byte of the arena's mmap. Long-offset arithmetic OK across <see cref="MaxSize"/>.</summary>
    public byte* BasePtr => _basePtr;

    /// <summary>Next-write offset. Mutated under the manager's lock during writer registration. Persisted in the file header.</summary>
    internal long Frontier { get; set; }

    /// <summary>
    /// Last value of <see cref="Frontier"/> reported to <c>Metrics.BlobAllocatedBytesByTier</c>.
    /// Lets <see cref="BlobArenaManager"/> push frontier deltas on
    /// <see cref="BlobArenaWriter.Complete"/> without re-counting bytes it already reported.
    /// </summary>
    internal long ReportedFrontier { get; set; }

    internal BlobArenaFile(PersistedSnapshotTier tier, ushort id, string path, long maxSize)
    {
        Tier = tier;
        BlobArenaId = id;
        Path = path;
        MaxSize = maxSize;
        Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Pre-extend to MaxSize (sparse via ftruncate) so the mapping is fixed for life and
        // appends never have to remap. The on-disk length therefore can no longer carry the
        // frontier — it is restored from the file header below.
        if (RandomAccess.GetLength(Handle) < maxSize)
            RandomAccess.SetLength(Handle, maxSize);
        OpenMmap(maxSize);

        // A fresh pre-extended file reads the header as 0 ⇒ frontier sits at HeaderSize (empty,
        // reusable). A rehydrated file restores the frontier the last writer persisted.
        Frontier = Math.Max(ReadFrontierHeader(), HeaderSize);
        ReportedFrontier = Frontier;
        Metrics.BlobFileCountByTier.AddOrUpdate(tier, 1L, static (_, c) => c + 1);
        // Allocated bytes are RLP data only — the fixed HeaderSize prefix is overhead, not data,
        // so the gauge returns to baseline when a file is reset to an empty (header-only) state.
        long allocated = Frontier - HeaderSize;
        if (allocated > 0)
            Metrics.BlobAllocatedBytesByTier.AddOrUpdate(tier,
                static (_, f) => f, static (_, b, f) => b + f, allocated);
    }

    [MemberNotNull(nameof(_mmf), nameof(_accessor))]
    private void OpenMmap(long size)
    {
        _mmf = MemoryMappedFile.CreateFromFile(Handle, mapName: null, size, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        _basePtr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        if (OperatingSystem.IsLinux())
            Madvise(_basePtr, (nuint)size, MADV_RANDOM);
    }

    private void CloseMmap()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _basePtr = null;
    }

    /// <summary>Read the persisted frontier from the file header (int64 little-endian at offset 0).</summary>
    private long ReadFrontierHeader() =>
        BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_basePtr, HeaderSize));

    /// <summary>
    /// Persist <paramref name="frontier"/> into the file header (int64 little-endian at offset 0).
    /// Written through the file handle; the caller makes it durable with <see cref="Fsync"/>.
    /// </summary>
    public void WriteFrontierHeader(long frontier)
    {
        Span<byte> buf = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteInt64LittleEndian(buf, frontier);
        RandomAccess.Write(Handle, buf, 0);
    }

    /// <summary>
    /// Zero-copy view over <c>[offset, offset + size)</c> of the mmap. Caller must already hold
    /// a lease and must have reported the access to the owning manager's
    /// <see cref="PageResidencyTracker"/> (see <see cref="BlobArenaManager.TouchBlobPage"/>).
    /// </summary>
    public ReadOnlySpan<byte> GetSpan(long offset, int size) => new(_basePtr + offset, size);

    /// <summary>
    /// Mark this file as "preserve on disk when its refcount hits zero". Set by
    /// <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> for every blob
    /// arena that a still-loaded snapshot references, so the file survives manager
    /// teardown and is rehydrated by the next session's <see cref="BlobArenaManager.Initialize"/>.
    /// Idempotent.
    /// </summary>
    public void PersistOnShutdown() => Interlocked.Exchange(ref _preserveOnDispose, 1);

    /// <summary>
    /// True iff <see cref="PersistOnShutdown"/> has been called for this file. Read by
    /// <see cref="BlobArenaManager.TryResetOrphanedFrontier"/> so an orphan-frontier reset
    /// does not punch a hole over a file the caller has promised to preserve across
    /// the next session — the file would survive on disk, but its bytes would be zeroed.
    /// </summary>
    internal bool IsShutdownPreserved => Volatile.Read(ref _preserveOnDispose) != 0;

    /// <summary>
    /// Defensive lease acquisition; returns false when the file has already entered
    /// <see cref="CleanUp"/>. Promotes <see cref="RefCountingDisposable.TryAcquireLease"/>
    /// from protected to internal so the owning manager can lease under its lock.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>
    /// True iff the file's refcount is exactly 1 — i.e. the only outstanding lease is
    /// the manager's array slot. Used by <see cref="BlobArenaManager.SweepUnreferenced"/>
    /// to detect post-restart orphans (Initialize-loaded files that no snapshot has
    /// leased) so the manager can drop its slot and let <see cref="CleanUp"/> delete
    /// the on-disk file.
    /// </summary>
    internal bool HasOnlyManagerLease => Volatile.Read(ref _leases.Value) == 1;

    /// <summary>
    /// Open a write stream seeked to <paramref name="startOffset"/>. Caller disposes when done.
    /// </summary>
    internal FileStream OpenWriteStream(long startOffset)
    {
        FileStream fs = new(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        fs.Seek(startOffset, SeekOrigin.Begin);
        return fs;
    }

    /// <summary>
    /// <c>madvise(MADV_DONTNEED)</c> over the page-aligned subrange of <c>[offset, offset+size)</c>,
    /// dropping the corresponding pages from the mmap's resident set.
    /// </summary>
    public void AdviseDontNeed(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;
        nuint pageSize = OsPageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;
        Madvise(_basePtr + start, end - start, MADV_DONTNEED);
    }

    /// <summary>
    /// <c>madvise(MADV_POPULATE_READ)</c> on the page-aligned subrange of <c>[offset, offset+size)</c>.
    /// On Linux ≥ 5.14 the kernel pre-faults the pages so the next read does not block on a page
    /// fault. On older kernels the call returns <c>EINVAL</c>, which is benign and ignored.
    /// </summary>
    public void PopulateRead(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;
        nuint pageSize = OsPageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;
        Madvise(_basePtr + start, end - start, MADV_POPULATE_READ);
    }

    /// <summary>
    /// Volatile single-byte read at <paramref name="offset"/> within this file's mmap. Used by
    /// the keep-warm path to refresh the kernel's LRU position on a resident page. Caller must
    /// hold a lease so <see cref="BasePtr"/> stays valid for the duration of the read — a
    /// userspace load on a torn-down mapping would SIGSEGV instead of returning a syscall error.
    /// </summary>
    public byte TouchByte(long offset) => Volatile.Read(ref *(_basePtr + offset));

    /// <summary>
    /// <c>fallocate(PUNCH_HOLE | KEEP_SIZE)</c> over the page-aligned subrange of
    /// <c>[offset, offset + size)</c>, freeing the dead range's disk blocks without changing
    /// the file length. Punched pages read back as zero through the mmap. Used to reclaim an
    /// orphaned file's data range without breaking the fixed-size mapping.
    /// </summary>
    internal PunchHoleOutcome PunchHole(long offset, long size) =>
        PosixReclaim.TryPunchHole((int)Handle.DangerousGetHandle(), offset, size);

    /// <summary>
    /// <c>fdatasync(2)</c> the underlying file — block until all previously written bytes
    /// (header and data) are durable on disk. Called by the persisted-snapshot convert path
    /// before the catalog records the new entry so a crash cannot leave the catalog pointing
    /// at unsynced pages.
    /// </summary>
    internal void Fsync() => PosixReclaim.Fsync((int)Handle.DangerousGetHandle());

    protected override void CleanUp()
    {
        CloseMmap();
        Handle.Dispose();
        // Preserve the on-disk file iff someone explicitly opted in via PersistOnShutdown;
        // otherwise delete it (the normal post-prune cleanup path).
        if (Volatile.Read(ref _preserveOnDispose) == 0)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
        Metrics.BlobFileCountByTier.AddOrUpdate(Tier,
            0L, static (_, c) => Math.Max(0, c - 1));
        long allocated = ReportedFrontier - HeaderSize;
        ReportedFrontier = 0;
        if (allocated > 0)
            Metrics.BlobAllocatedBytesByTier.AddOrUpdate(Tier,
                static (_, _) => 0L, static (_, b, r) => Math.Max(0, b - r), allocated);
    }
}
