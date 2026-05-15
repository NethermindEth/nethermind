// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
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
///       0x01 (StorageTopSubTag):      nested HSST (TreePath (3 bytes) → NodeRef, path length 0-5)
///       0x02 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → NodeRef, path length 8-15)
///       0x03 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → NodeRef, path length 16+)
///       0x04 (SlotSubTag):            nested HSST (SlotPrefix(30) → nested HSST(SlotSuffix(2) → SlotValue))
///       0x05 (AccountSubTag):         raw account slim RLP bytes (empty = deleted account)
///       0x06 (SelfDestructSubTag):    raw SD flag bytes (empty = destructed, 0x01 = new account)
///       0x07 (AddressSubTag):         raw 20-byte Address bytes — preimage of the outer addressHash
///   }
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

    // Sub-tags within per-address HSST (column 0x01), sorted byte order.
    internal static readonly byte[] StorageTopSubTag = [0x01];
    internal static readonly byte[] StorageCompactSubTag = [0x02];
    internal static readonly byte[] StorageFallbackSubTag = [0x03];
    internal static readonly byte[] SlotSubTag = [0x04];
    internal static readonly byte[] AccountSubTag = [0x05];
    internal static readonly byte[] SelfDestructSubTag = [0x06];
    internal static readonly byte[] AddressSubTag = [0x07];

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

    // Direct-mapped lock-free address-bound cache. Each slot is a single long:
    //   high 16 bits = bytes 4..6 of the address-hash (tag)
    //   low  48 bits = absolute offset of the LEB128 value-length byte in the outer
    //                  column 0x01 entry. 48 bits = 256 TiB, plenty.
    // Bucket index = bytes 0..4 of the address-hash (as uint32) masked by
    // (slotCount - 1). Bucket bits and tag bits are drawn from disjoint slices of
    // the Keccak hash so the tag's full 16 bits stay discriminating regardless of
    // cache size — if both came from the same slice, the tag's effective filtering
    // would shrink to (16 - log2(slotCount)) bits. The 32-bit bucket field
    // supports caches up to 2^32 slots without aliasing into the tag bytes.
    // Single-long Interlocked is intrinsic on every platform (no CMPXCHG16B needed).
    // Layout: keyFirst=false BTree entry shape is [Value][LEB128][FullKey]. On hit we
    // read 26 bytes at lebStart in one shot covering the LEB128 (≤ 6 bytes for any
    // realistic value length) followed by the 20-byte stored address-hash, then
    // compare to the lookup hash to catch tag collisions / layout drift. The cached
    // Bound is (lebStart - valueLength, valueLength).
    //
    // The slot array lives off-heap in a <see cref="NativeMemoryList{Int64}"/> sized
    // to the next power of two ≥ the snapshot's block span, capped at
    // AddressBoundCacheMaxSlots so the cache always fits in one 4 KiB page;
    // small-tier snapshots get no cache at all (field stays null). Demote
    // atomically swaps the field to null
    // and disposes — readers Volatile.Read once into a local so an in-flight call
    // can complete safely against the live array even if Demote runs concurrently.
    private const long AddressBoundCacheOffsetMask = (1L << 48) - 1;
    private const int AddressBoundCacheTagShift = 48;
    private const int AddressBoundCacheProbeBytes = 6 + AddressHashPrefixLength;
    // Cap the slot count so the cache fits in a single 4 KiB page (512 × 8 bytes).
    // Larger caches would smear lookups across multiple TLB entries with diminishing
    // hit-rate returns; the disk double-check picks up wherever the cache can't reach.
    private const int AddressBoundCacheMaxSlots = 512;
    private readonly int _addressBoundCacheMask;
    private NativeMemoryList<long>? _addressBoundCache;

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
    /// explicit eviction (e.g. the compactor, which lets <see cref="Demote"/> own this
    /// for sources) can pass <paramref name="adviseDontNeedOnDispose"/> = <c>false</c>
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
    /// <paramref name="tier"/> controls whether the address-bound cache is allocated.
    /// Only <see cref="PersistedSnapshotTier.Large"/> snapshots get a cache; small-tier
    /// snapshots (and small-tier compacted outputs) skip the allocation entirely. The
    /// cache slot count is the next power of two ≥ <c>to.BlockNumber - from.BlockNumber</c>,
    /// capped at <see cref="AddressBoundCacheMaxSlots"/> so longer-range snapshots scale
    /// up to the page-sized cap and no further.
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
            try
            {
                while (e.MoveNext())
                {
                    if (!_blobManager.TryLeaseFile(e.Current, out _))
                        throw new InvalidOperationException($"Blob arena {e.Current} not registered in this tier");
                    acquired++;
                }
            }
            finally { e.Dispose(); }
        }
        catch
        {
            int released = 0;
            RefIdsEnumerator e = GetRefIdsEnumerator();
            try
            {
                while (released < acquired && e.MoveNext())
                {
                    _blobManager.GetFile(e.Current).Dispose();
                    released++;
                }
            }
            finally { e.Dispose(); }
            _reservation.Dispose();
            throw;
        }

        if (tier == PersistedSnapshotTier.Large)
        {
            long blockSpan = to.BlockNumber - from.BlockNumber;
            if (blockSpan > 0)
            {
                int slotCount = Math.Min(
                    AddressBoundCacheMaxSlots,
                    (int)BitOperations.RoundUpToPowerOf2((uint)blockSpan));
                _addressBoundCache = new NativeMemoryList<long>(slotCount, slotCount);
                _addressBoundCacheMask = slotCount - 1;
            }
        }
    }

    /// <summary>
    /// Forward iterator over this snapshot's referenced blob arena ids. Reads
    /// the ref_ids HSST value little-endian-ushort at a time from a temporary
    /// <see cref="WholeReadSession"/>; the session is owned by the enumerator and
    /// released on <see cref="RefIdsEnumerator.Dispose"/> (called automatically by
    /// <c>foreach</c>).
    /// </summary>
    public RefIdsEnumerator GetRefIdsEnumerator() => new(this);

    /// <summary>
    /// Ref-struct enumerator backing <see cref="GetRefIdsEnumerator"/>. Yields each
    /// <see cref="NodeRef.BlobArenaId"/> stored in the snapshot's <c>ref_ids</c>
    /// metadata entry in ascending order without allocating a <c>ushort[]</c>.
    /// </summary>
    public ref struct RefIdsEnumerator
    {
        private WholeReadSession? _session;
        private long _cursor;
        private long _end;
        private ushort _current;

        internal RefIdsEnumerator(PersistedSnapshot snapshot)
        {
            _session = snapshot._reservation.BeginWholeReadSession();
            WholeReadSessionReader r = _session.GetReader();
            HsstReader<WholeReadSessionReader, NoOpPin> root = new(in r, new Bound(0, r.Length));
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
            if (_session is null || _cursor >= _end) return false;
            Span<byte> buf = stackalloc byte[2];
            WholeReadSessionReader r = _session.GetReader();
            if (!r.TryRead(_cursor, buf)) return false;
            _current = BinaryPrimitives.ReadUInt16LittleEndian(buf);
            _cursor += 2;
            return true;
        }

        public RefIdsEnumerator GetEnumerator() => this;

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
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
        // Snapshot the cache reference once: Demote may swap it to null concurrently,
        // but the NativeMemoryList instance we read here stays alive (its Dispose
        // is only called after a successful Interlocked.Exchange to null in Demote,
        // which races at most with reads that already captured the live ref).
        NativeMemoryList<long>? cache = Volatile.Read(ref _addressBoundCache);
        // Disjoint slices of the address-hash: bytes 0..4 (uint32) select the
        // bucket, bytes 4..6 (ushort) are the tag stored alongside the offset.
        // Disjoint bits keep the tag's full 16-bit entropy regardless of cache size.
        uint bucketBits = MemoryMarshal.Read<uint>(addressHash.Bytes);
        ushort hashTag = MemoryMarshal.Read<ushort>(addressHash.Bytes[4..]);
        if (cache is not null)
        {
            int idx = (int)(bucketBits & (uint)_addressBoundCacheMask);
            ref long slot = ref cache.GetRef(idx);

            long cached = Interlocked.Read(ref slot);
            ushort cachedTag = (ushort)(cached >>> AddressBoundCacheTagShift);
            long lebOffset = cached & AddressBoundCacheOffsetMask;
            if (cachedTag == hashTag && lebOffset != 0)
            {
                // Single read covers [LEB128 (≤ 6 bytes)][FullKey (20 bytes)]. The
                // LEB128 decodes the value length; the FullKey at probe[pos..pos+20]
                // is the stored 20-byte address-hash we double-check against.
                Span<byte> probe = stackalloc byte[AddressBoundCacheProbeBytes];
                if (reader.TryRead(lebOffset, probe))
                {
                    int pos = 0;
                    long valueLength = Leb128.Read(probe, ref pos);
                    if (probe.Slice(pos, AddressHashPrefixLength)
                            .SequenceEqual(addressHash.Bytes[..AddressHashPrefixLength]))
                    {
                        addressBound = new Bound(lebOffset - valueLength, valueLength);
                        return true;
                    }
                }
            }
        }

        if (!PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, in addressHash, out addressBound))
            return false;

        if (cache is not null)
        {
            // keyFirst=false bound is (lebStart - valueLength, valueLength), so
            // lebStart = bound.Offset + bound.Length.
            int idx = (int)(bucketBits & (uint)_addressBoundCacheMask);
            long newLebStart = addressBound.Offset + addressBound.Length;
            long newSlot = ((long)hashTag << AddressBoundCacheTagShift) | (newLebStart & AddressBoundCacheOffsetMask);
            Interlocked.Exchange(ref cache.GetRef(idx), newSlot);
        }
        return true;
    }

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

    /// <summary>
    /// Transfer this snapshot's address-bound cache entries into <paramref name="target"/>
    /// (typically a freshly-built compacted snapshot that supersedes this one), zero and
    /// dispose the local cache, then advise this snapshot's mmap pages cold. For each
    /// non-empty source slot we read the stored 20-byte address-hash from this snapshot's
    /// mmap and resolve it through <paramref name="target"/>'s normal lookup, which warms
    /// the target's cache as a side effect of the seek+populate path in
    /// <see cref="TryGetAddressBound"/>.
    /// </summary>
    /// <remarks>
    /// Safe to call once per snapshot. The cache field is atomically swapped to null before
    /// the walk so concurrent <see cref="TryGetAddressBound"/> calls that race with Demote
    /// either see the live cache (and complete normally against it) or see null and fall
    /// straight through to the seek path. Subsequent reads after Demote returns are
    /// cache-cold for this snapshot. <see cref="ArenaReservation.AdviseDontNeed"/> at the
    /// end issues <c>madvise(MADV_DONTNEED)</c> on the mmap range and clears the per-arena
    /// page-tracker entries — runs unconditionally so small-tier sources (no cache) still
    /// cold their pages on demote. No-op transfer when no cache was allocated.
    /// </remarks>
    public void Demote(PersistedSnapshot target)
    {
        NativeMemoryList<long>? cache = Interlocked.Exchange(ref _addressBoundCache, null);
        if (cache is not null)
        {
            try
            {
                ArenaByteReader sourceReader = CreateReader();
                ArenaByteReader targetReader = target.CreateReader();
                int n = cache.Count;
                Span<byte> probe = stackalloc byte[AddressBoundCacheProbeBytes];
                for (int i = 0; i < n; i++)
                {
                    long entry = cache[i];
                    long lebOffset = entry & AddressBoundCacheOffsetMask;
                    if (lebOffset == 0) continue;

                    if (!sourceReader.TryRead(lebOffset, probe)) continue;
                    int pos = 0;
                    _ = Leb128.Read(probe, ref pos);

                    ValueHash256 addressHash = default;
                    probe.Slice(pos, AddressHashPrefixLength).CopyTo(addressHash.BytesAsSpan);
                    target.TryGetAddressBound(in targetReader, in addressHash, out _);
                }
            }
            finally
            {
                // Zero the backing before NativeMemoryList.Dispose hands the (possibly
                // pinned ArrayPool) array back to the shared pool — pool consumers
                // expect a clean buffer.
                cache.AsSpan().Clear();
                cache.Dispose();
            }
        }

        _reservation.AdviseDontNeed();
    }

    protected override void CleanUp()
    {
        // Free the cache eagerly if Demote didn't already. Interlocked.Exchange matches
        // Demote's swap pattern; the ?.Dispose() handles both the post-Demote (null) and
        // never-allocated (small-tier) cases.
        Interlocked.Exchange(ref _addressBoundCache, null)?.Dispose();
        // Drain the iterator before disposing the reservation — the iterator owns a
        // WholeReadSession on _reservation, and this snapshot's own lease keeps the mmap
        // alive until both leases drop. GetFile is a lock-free array read; the lease we
        // acquired at construction kept the slot alive until now.
        foreach (ushort id in GetRefIdsEnumerator())
            _blobManager.GetFile(id).Dispose();
        _reservation.Dispose();
    }
}
