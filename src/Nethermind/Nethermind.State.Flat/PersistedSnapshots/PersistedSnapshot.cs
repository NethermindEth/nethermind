// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by columnar HSST metadata on disk. Trie-node RLP
/// values are not stored inline — every trie-node slot in the HSST holds an
/// 8-byte <see cref="NodeRef"/> pointing into a blob arena. The reservation
/// owned by this snapshot stores the metadata bytes only.
///
/// The outer HSST has 5 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, ref_ids list, state root values
///   Column 0x01: AddressHash (20 bytes, = Keccak(address)[..20]) → per-address HSST {
///       0x01 (AddressSubTag):         raw 20-byte Address bytes — preimage of the outer addressHash
///       0x02 (AccountSubTag):         raw account slim RLP bytes (empty = deleted account)
///       0x03 (SelfDestructSubTag):    raw SD flag bytes (empty = destructed, 0x01 = new account)
///       0x04 (SlotSubTag):            nested HSST (SlotPrefix(30) → nested HSST(SlotSuffix(2) → SlotValue))
///       0x05 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → NodeRef, path length 16+)
///       0x06 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → NodeRef, path length 8-15)
///       0x07 (StorageTopSubTag):      nested HSST (TreePath (3 bytes) → NodeRef, path length 0-5)
///   }
///   Sub-tag values are arranged so the small, hot metadata (Address/Account/SelfDestruct)
///   gets the lowest byte values. The per-address inner HSST is built as a dense-byte-index
///   whose value blobs are streamed high-tag → low-tag (descending) so the storage-trie
///   blobs land at the front of the data section and the hot metadata blobs land adjacent
///   to the trailing Ends[] table, sharing OS pages with the lookup-time read.
///   Column 0x03: TreePath (8 bytes compact) → NodeRef (path length 6-15)
///   Column 0x05: TreePath (3 bytes) → NodeRef (path length 0-5)
///   Column 0x06: TreePath.Path (32 bytes) + PathLength (1 byte) → NodeRef (path length 16+)
/// </summary>
public sealed class PersistedSnapshot : RefCountingDisposable
{
    // Tag prefixes for outer HSST columns
    internal static readonly byte[] MetadataTag = [0x00];
    internal static readonly byte[] AccountColumnTag = [0x01];
    internal static readonly byte[] StateNodeTag = [0x03];
    internal static readonly byte[] StateTopNodesTag = [0x05];
    internal static readonly byte[] StateNodeFallbackTag = [0x06];

    // Per-address column 0x01 outer key width — first 20 bytes of Keccak(address).
    internal const int AddressHashPrefixLength = 20;

    // Sub-tags within per-address HSST (column 0x01). The per-address HSST is built as a
    // dense-byte-index whose writer streams entries in strictly descending tag order, so the
    // value blobs for the hot small metadata (low tag values) end up adjacent to the trailing
    // Ends[] table — see the class-level remarks for the layout rationale.
    internal static readonly byte[] AddressSubTag = [0x01];
    internal static readonly byte[] AccountSubTag = [0x02];
    internal static readonly byte[] SelfDestructSubTag = [0x03];
    internal static readonly byte[] SlotSubTag = [0x04];
    internal static readonly byte[] StorageFallbackSubTag = [0x05];
    internal static readonly byte[] StorageCompactSubTag = [0x06];
    internal static readonly byte[] StorageTopSubTag = [0x07];

    // Single-byte companions of the sub-tag arrays above, consumed by the fast-path
    // <see cref="HsstDenseByteIndexReader.TryResolveSingleTag{TReader, TPin}"/> resolver which
    // takes the tag as a <see cref="byte"/> rather than a one-element <see cref="ReadOnlySpan{T}"/>.
    internal const byte AccountSubTagByte = 0x02;
    internal const byte SelfDestructSubTagByte = 0x03;
    internal const byte SlotSubTagByte = 0x04;
    internal const byte StorageFallbackSubTagByte = 0x05;
    internal const byte StorageCompactSubTagByte = 0x06;
    internal const byte StorageTopSubTagByte = 0x07;

    // Metadata column keys. The HSST builder requires uniform key length per HSST,
    // so the original ASCII keys are NUL-padded to a fixed 10 bytes (the longest
    // original key, "from_block"). NUL-padding preserves the original sort order
    // because no original key is a prefix of any other.
    internal const int MetadataKeyLength = 10;
    internal static readonly byte[] MetadataFromBlockKey = "from_block"u8.ToArray();
    internal static readonly byte[] MetadataFromHashKey = "from_hash\0"u8.ToArray();
    internal static readonly byte[] MetadataNodeRefsKey = "noderefs\0\0"u8.ToArray();
    internal static readonly byte[] MetadataRefIdsKey = "ref_ids\0\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToBlockKey = "to_block\0\0"u8.ToArray();
    internal static readonly byte[] MetadataToHashKey = "to_hash\0\0\0"u8.ToArray();
    internal static readonly byte[] MetadataVersionKey = "version\0\0\0"u8.ToArray();

    // Single 8-way set-associative clock (second-chance) address-bound cache mirroring
    // <see cref="PageResidencyTracker"/>'s hot/miss-path split. One set ⇒ 8 ways × 8 bytes
    // = 64 bytes stored inline as a <see cref="Vector512{T}"/> field directly on the
    // snapshot — no separate heap allocation. The runtime gives <see cref="Vector512{T}"/>
    // its natural 64-byte alignment for the field offset within the object, matching the
    // single-cache-line layout the previous <see cref="NativeMemory.AlignedAlloc(nuint,nuint)"/>
    // -based variant relied on. The <see cref="Vector512{T}"/> is never used as a SIMD
    // vector here — it is purely an alignment-bearing 64-byte storage cell, reinterpreted
    // as <c>Span&lt;long&gt;</c> via <see cref="MemoryMarshal.CreateSpan{T}(ref T,int)"/>.
    //
    // Each slot packs:
    //   bit 63: REF — armed on every hit and insert, cleared by the clock hand on a miss-pass.
    //   bit 62: VALID — distinguishes an empty (0L) slot from a stored (tag=0, offset=0) entry.
    //   bits 46..61: 16-bit tag (bytes 4..6 of the address-hash).
    //   bits 0..45: 46-bit absolute offset of the LEB128 value-length byte in the outer
    //               column 0x01 entry. 46 bits = 64 TiB, ample for any real snapshot.
    // Layout: keyFirst=false BTree entry shape is [Value][LEB128][FullKey]. On a tag match
    // we read 26 bytes at lebStart covering the LEB128 (≤ 6 bytes) plus the 20-byte stored
    // address-hash, then compare to the lookup hash to catch tag collisions / layout drift.
    // The cached Bound is (lebStart - valueLength, valueLength).
    //
    // Hot path: lock-free 8-way Volatile.Read scan; <see cref="Interlocked.Or"/> re-arms REF
    // after the disk probe confirms the cached tag isn't a collision. Miss path: take the
    // 1-bit spin-lock in <see cref="_addressBoundCacheMeta"/> (also holding the 3-bit clock
    // hand), re-scan for an existing matching entry, then for an empty way, then advance
    // the clock hand clearing REF bits until an unreferenced way is evicted.
    private const long AddressBoundCacheRefBit = unchecked((long)0x8000_0000_0000_0000UL);
    private const long AddressBoundCacheValidBit = 0x4000_0000_0000_0000L;
    private const long AddressBoundCacheKeyMask = ~AddressBoundCacheRefBit;
    private const long AddressBoundCacheOffsetMask = (1L << 46) - 1;
    private const int AddressBoundCacheTagShift = 46;
    private const int AddressBoundCacheWays = 8;
    private const int AddressBoundCacheWayMask = AddressBoundCacheWays - 1;
    private const int AddressBoundCacheMetaLockBit = 1 << 7;
    private const int AddressBoundCacheMetaHandMask = 0x7;
    private const int AddressBoundCacheProbeBytes = 6 + AddressHashPrefixLength;

    private Vector512<long> _addressBoundCache;
    private int _addressBoundCacheMeta;

    private readonly ArenaReservation _reservation;
    // Manager that owns the per-id blob arena slots. The repository acquires one lease per
    // referenced id before this ctor runs and releases them in CleanUp / PersistOnShutdown,
    // resolving each id via _blobManager.GetFile(id) (lock-free O(1) array read). The
    // canonical list of leased ids lives on disk inside this snapshot's metadata HSST under
    // the "ref_ids" key — no in-memory dict.
    private readonly IBlobArenaManager _blobManager;

    public StateId From { get; }
    public StateId To { get; }

    public long Size => _reservation.Size;

    internal ArenaReservation Reservation => _reservation;

    /// <summary>
    /// Begin a scoped whole-buffer read over this snapshot's reservation. By default the
    /// session madvises the mmap range cold on dispose; callers that perform their own
    /// explicit eviction can pass <paramref name="adviseDontNeedOnDispose"/> = <c>false</c>
    /// to avoid a redundant <c>madvise</c> syscall.
    /// </summary>
    public WholeReadSession BeginWholeReadSession(bool adviseDontNeedOnDispose = true) =>
        _reservation.BeginWholeReadSession(adviseDontNeedOnDispose);

    /// <summary>
    /// Construct a reader over this snapshot's bytes.
    /// </summary>
    internal ArenaByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Construct a snapshot over a pre-leased metadata reservation. The caller (typically
    /// <see cref="PersistedSnapshotRepository"/>) MUST have already acquired one lease per
    /// blob arena id referenced by the snapshot's <c>ref_ids</c> metadata via
    /// <see cref="IBlobArenaManager.TryLeaseFile"/>, and is responsible for rolling those
    /// leases back on construction failure. This ctor just bumps the metadata reservation
    /// lease and stashes the manager ref for later id → file resolution.
    /// </summary>
    /// <remarks>
    /// The address-bound cache is enabled on every snapshot regardless of <paramref name="tier"/>:
    /// the slot storage is inline as a <see cref="Vector512{T}"/> field (64-byte aligned)
    /// so there is no per-snapshot allocation to skip. <paramref name="tier"/> is retained
    /// for caller compatibility but no longer affects the cache.
    /// </remarks>
    public PersistedSnapshot(StateId from, StateId to, ArenaReservation reservation,
        IBlobArenaManager blobManager, PersistedSnapshotTier tier)
    {
        From = from;
        To = to;
        _reservation = reservation;
        _blobManager = blobManager;
        _reservation.AcquireLease();

        // Walk the on-disk ref_ids stream once and lease each referenced blob arena file.
        // The snapshot now owns the lease lifecycle: CleanUp / PersistOnShutdown re-walk
        // the same iterator to release / persist on shutdown. On partial failure we walk
        // the prefix we already acquired and drop those leases before unwinding the
        // metadata reservation's lease and rethrowing.
        int acquired = 0;
        try
        {
            RefIdsEnumerator e = GetRefIdsEnumerator();
            while (e.MoveNext())
            {
                if (!_blobManager.TryLeaseFile(e.Current, out _))
                    throw new InvalidOperationException($"Blob arena {e.Current} not registered in this tier");
                acquired++;
            }
        }
        catch
        {
            int released = 0;
            RefIdsEnumerator e = GetRefIdsEnumerator();
            while (released < acquired && e.MoveNext())
            {
                _blobManager.GetFile(e.Current).Dispose();
                released++;
            }
            _reservation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Forward iterator over this snapshot's referenced blob arena ids. Reads
    /// the ref_ids HSST value little-endian-ushort at a time.
    /// </summary>
    /// <remarks>
    /// Backed by a plain <see cref="ArenaByteReader"/> over the snapshot's reservation
    /// rather than a <see cref="WholeReadSession"/>: ref_ids is a tiny, frequently-accessed
    /// metadata entry that fits in a single OS page, so the page-residency tracker (touched
    /// on each <c>ArenaByteReader.TryRead</c>) is the right consumer of these reads. A
    /// session would either bypass the tracker and drop pages from the kernel page cache on
    /// dispose, or skip the dispose-time <c>MADV_DONTNEED</c> only to keep paying for the
    /// per-session mmap view + lease bookkeeping for a 2-byte read. The reader holds no
    /// resources of its own; the surrounding snapshot's lease keeps the mmap alive.
    /// </remarks>
    public RefIdsEnumerator GetRefIdsEnumerator() => new(this);

    /// <summary>
    /// Ref-struct enumerator backing <see cref="GetRefIdsEnumerator"/>. Yields each
    /// <see cref="NodeRef.BlobArenaId"/> stored in the snapshot's <c>ref_ids</c>
    /// metadata entry in ascending order without allocating a <c>ushort[]</c>.
    /// </summary>
    public ref struct RefIdsEnumerator
    {
        private ArenaByteReader _reader;
        private long _cursor;
        private long _end;
        private ushort _current;

        internal RefIdsEnumerator(PersistedSnapshot snapshot)
        {
            _reader = snapshot._reservation.CreateReader();
            HsstReader<ArenaByteReader, NoOpPin> root = new(in _reader, new Bound(0, _reader.Length));
            if (root.TrySeek(MetadataTag, out _) &&
                root.TrySeek(MetadataRefIdsKey, out Bound rb) &&
                rb.Length > 0 && rb.Length % 2 == 0)
            {
                _cursor = rb.Offset;
                _end = rb.Offset + rb.Length;
            }
        }

        public readonly ushort Current => _current;

        public bool MoveNext()
        {
            if (_cursor >= _end) return false;
            Span<byte> buf = stackalloc byte[2];
            if (!_reader.TryRead(_cursor, buf)) return false;
            _current = BinaryPrimitives.ReadUInt16LittleEndian(buf);
            _cursor += 2;
            return true;
        }

        public RefIdsEnumerator GetEnumerator() => this;
    }

    /// <summary>
    /// Materialise the trie-node RLP at <paramref name="localBound"/>. The bound holds a
    /// 6-byte <see cref="NodeRef"/>; the actual RLP bytes live in a blob arena.
    /// </summary>
    internal byte[] ResolveTrieRlp(Bound localBound)
    {
        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        Span<byte> nr = nrBuf[..checked((int)localBound.Length)];
        ArenaByteReader reader = _reservation.CreateReader();
        reader.TryRead(localBound.Offset, nr);
        NodeRef nodeRef = NodeRef.Read(nr);
        return ReadBlobArenaRlp(nodeRef.BlobArenaId, nodeRef.RlpDataOffset);
    }

    private bool TryGetAddressBound(in ArenaByteReader reader, in ValueHash256 addressHash, out Bound addressBound)
    {
        Span<long> slots = MemoryMarshal.CreateSpan(
            ref Unsafe.As<Vector512<long>, long>(ref _addressBoundCache), AddressBoundCacheWays);
        ushort hashTag = MemoryMarshal.Read<ushort>(addressHash.Bytes[4..6]);
        // Lock-free 8-way scan: a tag match is a candidate, still verified against the
        // 20-byte stored address-hash on disk to filter out the inevitable collisions.
        for (int w = 0; w < AddressBoundCacheWays; w++)
        {
            long s = Volatile.Read(ref slots[w]);
            if ((s & AddressBoundCacheValidBit) == 0) continue;
            if ((ushort)((s >>> AddressBoundCacheTagShift) & 0xFFFF) != hashTag) continue;

            long lebOffset = s & AddressBoundCacheOffsetMask;
            Span<byte> probe = stackalloc byte[AddressBoundCacheProbeBytes];
            if (!reader.TryRead(lebOffset, probe)) continue;
            int pos = 0;
            long valueLength = Leb128.Read(probe, ref pos);
            if (!probe.Slice(pos, AddressHashPrefixLength)
                    .SequenceEqual(addressHash.Bytes[..AddressHashPrefixLength]))
                continue;

            if ((s & AddressBoundCacheRefBit) == 0)
                Interlocked.Or(ref slots[w], AddressBoundCacheRefBit);
            addressBound = new Bound(lebOffset - valueLength, valueLength);
            return true;
        }

        if (!PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, in addressHash, out addressBound))
            return false;

        // keyFirst=false bound is (lebStart - valueLength, valueLength), so
        // lebStart = bound.Offset + bound.Length.
        long newLebStart = addressBound.Offset + addressBound.Length;
        long newEntry = AddressBoundCacheValidBit
                      | AddressBoundCacheRefBit
                      | ((long)hashTag << AddressBoundCacheTagShift)
                      | (newLebStart & AddressBoundCacheOffsetMask);
        InsertAddressBound(newEntry);
        return true;
    }

    private void InsertAddressBound(long newEntry)
    {
        ref int meta = ref _addressBoundCacheMeta;
        AcquireAddressBoundCacheLock(ref meta);
        try
        {
            Span<long> slots = MemoryMarshal.CreateSpan(
                ref Unsafe.As<Vector512<long>, long>(ref _addressBoundCache), AddressBoundCacheWays);
            // Re-scan under the lock — another miss-path racer may already have installed
            // this exact (tag, offset) pair, in which case just re-arm its REF bit.
            for (int w = 0; w < AddressBoundCacheWays; w++)
            {
                long s = slots[w];
                if ((s & AddressBoundCacheKeyMask) == (newEntry & AddressBoundCacheKeyMask))
                {
                    Volatile.Write(ref slots[w], s | AddressBoundCacheRefBit);
                    return;
                }
            }

            // Look for an empty way (VALID=0). New arrivals already carry REF=1 in
            // <paramref name="newEntry"/> so they survive the first clock pass.
            for (int w = 0; w < AddressBoundCacheWays; w++)
            {
                if (slots[w] == 0L)
                {
                    Volatile.Write(ref slots[w], newEntry);
                    return;
                }
            }

            // Set is full — run the clock. Worst case: 8 set-REFs ⇒ one full pass clears
            // them, the second pass finds an unreferenced way. Bound at 2*Ways iterations.
            int hand = meta & AddressBoundCacheMetaHandMask;
            for (int i = 0; i < 2 * AddressBoundCacheWays; i++)
            {
                long s = slots[hand];
                if ((s & AddressBoundCacheRefBit) != 0)
                {
                    Volatile.Write(ref slots[hand], s & ~AddressBoundCacheRefBit);
                    hand = (hand + 1) & AddressBoundCacheWayMask;
                    continue;
                }

                Volatile.Write(ref slots[hand], newEntry);
                hand = (hand + 1) & AddressBoundCacheWayMask;
                meta = (meta & ~AddressBoundCacheMetaHandMask) | hand;
                return;
            }

            Debug.Fail("Clock scan failed to find a victim");
        }
        finally
        {
            ReleaseAddressBoundCacheLock(ref meta);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireAddressBoundCacheLock(ref int meta)
    {
        SpinWait spinner = default;
        while (true)
        {
            int observed = Volatile.Read(ref meta);
            if ((observed & AddressBoundCacheMetaLockBit) == 0)
            {
                int withLock = observed | AddressBoundCacheMetaLockBit;
                if (Interlocked.CompareExchange(ref meta, withLock, observed) == observed)
                    return;
            }
            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseAddressBoundCacheLock(ref int meta) =>
        Volatile.Write(ref meta, meta & ~AddressBoundCacheMetaLockBit);

    public bool TryGetAccount(in ValueHash256 addressHash, out Account? account)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, in addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetAccount<ArenaByteReader, NoOpPin>(in reader, addrBound, out Bound b))
        {
            account = null;
            return false;
        }
        int bLenInt = checked((int)b.Length);
        Span<byte> buf = bLenInt <= 256 ? stackalloc byte[256] : new byte[bLenInt];
        Span<byte> rlp = buf[..bLenInt];
        reader.TryRead(b.Offset, rlp);
        if (rlp.Length == 1 && rlp[0] == 0x00)
        {
            account = null;
            return true;
        }
        Rlp.ValueDecoderContext ctx = new(rlp);
        account = AccountDecoder.Slim.Decode(ref ctx);
        return true;
    }

    public bool TryGetSlot(in ValueHash256 addressHash, in UInt256 index, ref SlotValue slotValue)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, in addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetSlot<ArenaByteReader, NoOpPin>(in reader, addrBound, in index, out Bound b))
            return false;
        Span<byte> buf = stackalloc byte[32];
        Span<byte> raw = buf[..checked((int)b.Length)];
        reader.TryRead(b.Offset, raw);
        slotValue = SlotValue.FromSpanWithoutLeadingZero(raw);
        return true;
    }

    public bool? TryGetSelfDestructFlag(in ValueHash256 addressHash)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, in addressHash, out Bound addrBound))
            return null;
        return PersistedSnapshotReader.TryGetSelfDestructFlag<ArenaByteReader, NoOpPin>(in reader, addrBound);
    }

    public bool TryLoadStateNodeRlp(scoped in TreePath path, out byte[]? nodeRlp)
    {
        ArenaByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryLoadStateNodeRlp<ArenaByteReader, NoOpPin>(in reader, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveTrieRlp(bound);
        return true;
    }

    public bool TryLoadStorageNodeRlp(in ValueHash256 addressHash, in TreePath path, out byte[]? nodeRlp)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, in addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryLoadStorageNodeRlpInBound<ArenaByteReader, NoOpPin>(in reader, addrBound, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveTrieRlp(bound);
        return true;
    }

    /// <summary>
    /// Read the "ref_ids" list from a snapshot's metadata column as a fresh
    /// <c>ushort[]</c>. Production code on the snapshot life-cycle path iterates via
    /// <see cref="GetRefIdsEnumerator"/> instead; this method is preserved for test
    /// assertions that need a materialised array to compare against.
    /// </summary>
    public static ushort[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct =>
        PersistedSnapshotReader.ReadRefIdsFromMetadata<TReader, TPin>(in reader);

    // Worst-case Merkle-Patricia branch node: 17 entries × (1-byte prefix + 32-byte hash)
    // plus a 3-byte long-list framing header ≈ 564 bytes. Round up to 568 so the read
    // covers any branch node in one pread.
    private const int MaxTrieNodeRlpBytes = 568;

    private byte[] ReadBlobArenaRlp(ushort blobArenaId, int offset)
    {
        BlobArenaFile file = _blobManager.GetFile(blobArenaId);
        using NativeMemoryList<byte> rented = new(MaxTrieNodeRlpBytes, MaxTrieNodeRlpBytes);
        Span<byte> buf = rented.AsSpan();
        int bytesRead = file.RandomRead(offset, buf);
        Rlp.ValueDecoderContext ctx = new(buf[..bytesRead]);
        int totalLength = ctx.PeekNextRlpLength();
        byte[] result = new byte[totalLength];
        buf[..totalLength].CopyTo(result);
        return result;
    }

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    /// <summary>
    /// Drop this snapshot's pages from the arena's <see cref="PageResidencyTracker"/> without
    /// re-issuing <c>madvise(MADV_DONTNEED)</c>. Use after a code path that has already
    /// advised the same range (e.g. a freshly-closed <see cref="WholeReadSession"/>) and
    /// only needs the tracker bookkeeping cleared.
    /// </summary>
    public void ForgetTracker() => _reservation.ForgetTracker();

    public bool TryAcquire() => TryAcquireLease();

    /// <summary>
    /// Advise this snapshot's mmap range cold (<c>madvise(MADV_DONTNEED)</c>) and clear
    /// the per-arena page-tracker entries that cover it. Intended as a hook for callers
    /// that have superseded this snapshot but want to drop its resident pages eagerly
    /// rather than waiting for full disposal — e.g. the compactor releasing sources
    /// after merging them into a new snapshot.
    /// </summary>
    /// <remarks>
    /// Does not touch the inline address-bound cache: its 64 bytes stay on the snapshot
    /// and the cached offsets remain content-verified against the (now-cold) mmap range,
    /// so subsequent reads still hit the cache and simply pay a cold-page fault on first
    /// access. Idempotent and safe to call from any thread.
    /// </remarks>
    public void Demote() => _reservation.AdviseDontNeed();

    /// <summary>
    /// Mark every file this snapshot references (its metadata <see cref="ArenaReservation"/>'s
    /// <see cref="ArenaFile"/> and every leased <see cref="BlobArenaFile"/>) for
    /// shutdown-preservation. Called by <see cref="PersistedSnapshotRepository.Dispose"/>
    /// before tearing down loaded snapshots so their on-disk data survives into the next
    /// session. Reads the leased id list from the metadata HSST on each call; idempotent
    /// and safe to call from any thread.
    /// </summary>
    public void PersistOnShutdown()
    {
        _reservation.PersistOnShutdown();
        foreach (ushort id in GetRefIdsEnumerator())
            _blobManager.GetFile(id).PersistOnShutdown();
    }

    protected override void CleanUp()
    {
        // Drain the iterator before disposing the reservation — the iterator reads through
        // the reservation's mmap via an ArenaByteReader, and this snapshot's own lease
        // (acquired at construction) keeps the mmap alive until it drops at the end of
        // CleanUp. GetFile is a lock-free array read; the lease we acquired at construction
        // kept the slot alive until now.
        foreach (ushort id in GetRefIdsEnumerator())
            _blobManager.GetFile(id).Dispose();
        _reservation.Dispose();
    }
}
