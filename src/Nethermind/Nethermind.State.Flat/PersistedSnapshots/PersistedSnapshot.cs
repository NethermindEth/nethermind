// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by columnar HSST metadata on disk. Trie-node RLP
/// values are not stored inline — every trie-node slot in the HSST holds an
/// 8-byte <see cref="NodeRef"/> pointing into a blob arena. The reservation
/// owned by this snapshot stores the metadata bytes only.
/// </summary>
/// <remarks>
/// On-disk vocabulary (column tags, sub-tags, metadata keys, value markers) is defined in
/// <see cref="PersistedSnapshotTags"/>; the columnar layout is documented there.
/// </remarks>
public sealed class PersistedSnapshot : SmallRefCountingDisposable
{

    // Window pre-faulted (one MADV_POPULATE_READ) at the tail of the bound on an address-bound
    // cache miss, so the rest of the inner-HSST walk reads an already-resident span.
    private const long AddressBoundWarmupBytes = 32 * 1024;

    private AddressBoundCache _addrCache;

    // Cached address-column BTree root, snapshotted at construction (the column is immutable for
    // the snapshot's life). Length == 0 = no address column.
    private readonly Bound _addressBtreeBound;
    private readonly long _addressBtreeRootStart;
    private readonly byte[] _addressBtreeRootPrefix = [];

    // Scope of the metadata column (tag 0x00), resolved once at construction. ReadBlobRange and
    // every ref_ids walk (construction, CleanUp, PersistOnShutdown) seek within it instead of
    // re-walking the HSST root each time. Length == 0 = column absent.
    private readonly Bound _metadataScope;

    private readonly ArenaReservation _reservation;
    // Metric label (tier + compact size) for the per-(tier, size) ActivePersistedSnapshotCount gauge.
    private readonly PersistedSnapshotLabel _label;
    // Each id is resolved on demand via _blobManager.GetFile(id), a lock-free O(1) array read:
    // the manager keys files by a dense int id in a direct array, so the per-snapshot lookup
    // cost is negligible and there is no need to carry a Dictionary<int, BlobArenaFile> on every
    // snapshot. The canonical leased-id list lives on disk in this snapshot's metadata HSST
    // under the "ref_ids" key.
    private readonly BlobArenaManager _blobManager;

    public StateId From { get; }
    public StateId To { get; }

    // Unified bloom gating all reads of this snapshot (address / slot / self-destruct keys and
    // state- / storage-trie paths in one filter). Owned by the snapshot — the keep-alive lease
    // keeps it alive and CleanUp disposes it. Defaults to the AlwaysTrue sentinel (never a false
    // negative) until the real filter is set via SetBloom at convert / merge time or on reload.
    private BloomFilter _bloom;
    public BloomFilter Bloom => _bloom;

    /// <summary>
    /// Swap in the unified bloom for this snapshot, disposing whatever filter it carried
    /// before. Used by the reload path, which constructs every snapshot first (with the
    /// AlwaysTrue placeholder) and only then rebuilds the real blooms.
    /// </summary>
    public void SetBloom(BloomFilter bloom)
    {
        BloomFilter previous = Interlocked.Exchange(ref _bloom, bloom);
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, bloom.DataBytes - previous.DataBytes);
        previous.Dispose();
    }

    /// <summary>
    /// The contiguous trie-RLP region this snapshot occupies in its blob arena, used to prefetch
    /// the whole region in one bulk read-ahead (<see cref="AdviseWillNeedBlobRange"/>) when a
    /// CompactSized snapshot is persisted — its scattered <c>NodeRef</c> reads then stream from
    /// already-warm pages. Non-empty only for base snapshots (which write all their RLPs through
    /// one <see cref="BlobArenaWriter"/>); <see cref="BlobRange.None"/> for compacted /
    /// CompactSized snapshots, whose <c>NodeRef</c>s scatter across many blob arenas.
    /// </summary>
    /// <remarks>
    /// Read once at construction from this snapshot's own metadata HSST (the <c>blob_range</c>
    /// key in column 0x00). A snapshot whose metadata carries no <c>blob_range</c> key resolves
    /// to <see cref="BlobRange.None"/>.
    /// </remarks>
    public BlobRange BlobRange { get; }

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

    private ArenaByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Construct a snapshot over a pre-leased metadata reservation. The caller (typically
    /// <see cref="PersistedSnapshotRepository"/>) MUST have already acquired one lease per
    /// blob arena id referenced by the snapshot's <c>ref_ids</c> metadata via
    /// <see cref="BlobArenaManager.TryLeaseFile"/>, and is responsible for rolling those
    /// leases back on construction failure. This ctor just bumps the metadata reservation
    /// lease and stashes the manager ref for later id → file resolution.
    /// </summary>
    /// <param name="tier">The persisted tier this snapshot belongs to, for the per-(tier, size)
    /// <see cref="Metrics.ActivePersistedSnapshotCount"/> gauge.</param>
    /// <param name="bloom">The unified bloom this snapshot takes ownership of, disposed with
    /// the snapshot. <c>null</c> installs the AlwaysTrue sentinel — correct (no false
    /// negatives) but unfiltered — for callers that populate the real bloom later via
    /// <see cref="SetBloom"/>.</param>
    public PersistedSnapshot(StateId from, StateId to, ArenaReservation reservation,
        BlobArenaManager blobManager, SnapshotTier tier, BloomFilter? bloom = null)
    {
        From = from;
        To = to;
        _reservation = reservation;
        _label = new PersistedSnapshotLabel(tier.MetricTierLabel(), to.BlockNumber - from.BlockNumber);
        _blobManager = blobManager;
        _bloom = bloom ?? BloomFilter.AlwaysTrue();
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, _bloom.DataBytes);
        _reservation.AcquireLease();

        // Walk the on-disk ref_ids stream once and lease each referenced blob arena file.
        // The snapshot now owns the lease lifecycle: CleanUp / PersistOnShutdown re-walk
        // the same iterator to release / persist on shutdown. On partial failure we walk
        // the prefix we already acquired and drop those leases before unwinding the
        // metadata reservation's lease and rethrowing.
        int acquired = 0;
        try
        {
            ArenaByteReader metaReader = _reservation.CreateReader();
            HsstReader<ArenaByteReader, NoOpPin> metaRoot = new(in metaReader, new Bound(0, metaReader.Length));
            _metadataScope = metaRoot.TrySeek(PersistedSnapshotTags.MetadataTag, out Bound metaScope) ? metaScope : default;

            BlobRange = ReadBlobRange(in metaReader);

            RefIdsEnumerator<ArenaByteReader, NoOpPin> e = GetRefIdsEnumerator();
            while (e.MoveNext())
            {
                if (!_blobManager.TryLeaseFile(e.Current, out _))
                    throw new InvalidOperationException($"Blob arena {e.Current} not registered with the blob manager");
                acquired++;
            }

            // Cache the address-column BTree root for the TryGetAddressBound miss path. A missing
            // column or unreadable trailer leaves the cache empty and the miss path returns "no entry".
            ArenaByteReader probeReader = _reservation.CreateReader();
            if (PersistedSnapshotReader.TryGetAddressColumnBound<ArenaByteReader, NoOpPin>(
                    in probeReader, out Bound addrColBound) &&
                addrColBound.Length >= 5 + 12)
            {
                Span<byte> tailBuf = stackalloc byte[5];
                if (probeReader.TryRead(addrColBound.Offset + addrColBound.Length - 5, tailBuf))
                {
                    int rootPrefixLen = tailBuf[0];
                    int rootSize = BinaryPrimitives.ReadUInt16LittleEndian(tailBuf.Slice(1, 2));
                    // tailBuf[3] is the trailer key length — fixed at AddressKeyLength (= 20)
                    // for column 0x01; the miss path passes the constant rather than caching it.
                    byte[] rootPrefix = [];
                    bool prefixOk = true;
                    if (rootPrefixLen > 0)
                    {
                        rootPrefix = new byte[rootPrefixLen];
                        prefixOk = probeReader.TryRead(
                            addrColBound.Offset + addrColBound.Length - 5 - rootPrefixLen, rootPrefix);
                    }
                    if (prefixOk)
                    {
                        long trailerLen = 5L + rootPrefixLen;
                        _addressBtreeBound = addrColBound;
                        _addressBtreeRootStart = addrColBound.Offset + addrColBound.Length - trailerLen - rootSize;
                        _addressBtreeRootPrefix = rootPrefix;
                    }
                }
            }
        }
        catch
        {
            int released = 0;
            RefIdsEnumerator<ArenaByteReader, NoOpPin> e = GetRefIdsEnumerator();
            while (released < acquired && e.MoveNext())
            {
                _blobManager.GetFile(e.Current).Dispose();
                released++;
            }
            Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -_bloom.DataBytes);
            _bloom.Dispose();
            _reservation.Dispose();
            throw;
        }

        // Increment only after every throw path above has been cleared, so a
        // partial-construction failure does not leave the gauge off by one.
        Metrics.ActivePersistedSnapshotCount.AddBy(_label, 1);
    }

    /// <summary>
    /// Forward iterator over this snapshot's referenced blob arena ids, reading the ref_ids HSST
    /// value a little-endian ushort at a time. Used during construction, <see cref="CleanUp"/> and
    /// <see cref="PersistOnShutdown"/> to walk the leased ids. Backed by a plain
    /// <see cref="ArenaByteReader"/> (not a <see cref="WholeReadSession"/>) that holds no resources
    /// of its own — the surrounding snapshot's lease keeps the mmap alive.
    /// </summary>
    private RefIdsEnumerator<ArenaByteReader, NoOpPin> GetRefIdsEnumerator() => new(_reservation.CreateReader(), _metadataScope);

    /// <summary>
    /// Read the <c>blob_range</c> metadata entry (column 0x00) — the contiguous trie-RLP run
    /// recorded by base snapshots. Returns <see cref="BlobRange.None"/> when the key is absent
    /// (compacted / CompactSized snapshots) or malformed.
    /// </summary>
    private BlobRange ReadBlobRange(scoped in ArenaByteReader reader)
    {
        if (_metadataScope.Length == 0) return BlobRange.None;
        HsstReader<ArenaByteReader, NoOpPin> meta = new(in reader, _metadataScope);
        if (meta.TrySeek(PersistedSnapshotTags.MetadataBlobRangeKey, out Bound b) &&
            b.Length == BlobRange.SerializedSize)
        {
            BlobRange range = default;
            if (reader.TryRead(b.Offset, MemoryMarshal.AsBytes(new Span<BlobRange>(ref range))))
                return range;
        }
        return BlobRange.None;
    }

    /// <summary>
    /// Ref-struct enumerator backing <see cref="GetRefIdsEnumerator"/>. Yields each
    /// <see cref="NodeRef.BlobArenaId"/> stored in the snapshot's <c>ref_ids</c>
    /// metadata entry in ascending order without allocating a <c>ushort[]</c>. Generic over
    /// the byte source — production drives it with the reservation's <see cref="ArenaByteReader"/>.
    /// </summary>
    private ref struct RefIdsEnumerator<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        private TReader _reader;
        private long _cursor;
        private long _end;
        private ushort _current;

        internal RefIdsEnumerator(TReader reader, Bound metadataScope)
        {
            _reader = reader;
            if (metadataScope.Length == 0) return;
            HsstReader<TReader, TPin> meta = new(in _reader, metadataScope);
            if (meta.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out Bound rb) &&
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
            if (!_reader.TryRead(_cursor, MemoryMarshal.AsBytes(new Span<ushort>(ref _current)))) return false;
            _cursor += 2;
            return true;
        }

        public RefIdsEnumerator<TReader, TPin> GetEnumerator() => this;
    }

    /// <summary>
    /// Resolve the per-address inner-HSST bound, going through the inline 8-way address-bound
    /// cache. <paramref name="useSpanReader"/> is set to <c>true</c> when the caller should
    /// drive the sub-tag walk over a zero-touch <see cref="SpanByteReader"/> sliced from the
    /// arena, skipping per-read page-tracker probes. Two regimes set it:
    /// <list type="bullet">
    ///   <item><b>Cache miss</b> — the warmup window covered the entire bound (i.e.
    ///     <c>addressBound.Length &lt;= <see cref="AddressBoundWarmupBytes"/></c>); every page
    ///     of the bound is now resident.</item>
    ///   <item><b>Cache hit</b> — the bound fits in the same threshold. We did not pre-fault,
    ///     but the cache hit implies the address was accessed recently; we accept the risk of
    ///     an inline page fault on a cold tail in exchange for skipping the per-read tracker
    ///     overhead.</item>
    /// </list>
    /// When the bound exceeds the threshold the caller stays on the page-tracker-backed
    /// <see cref="ArenaByteReader"/>.
    /// </summary>
    private bool TryGetAddressBound(in ArenaByteReader reader, Address address,
        out Bound addressBound, out bool useSpanReader)
    {
        useSpanReader = false;
        if (_addrCache.TryGet(in reader, address, out addressBound))
        {
            useSpanReader = addressBound.Length <= AddressBoundWarmupBytes;
            return true;
        }

        if (_addressBtreeBound.Length == 0)
        {
            addressBound = default;
            return false;
        }
        if (!HsstBTreeReader.TrySeekFromRoot<ArenaByteReader, NoOpPin>(
                in reader, _addressBtreeBound, _addressBtreeRootStart,
                _addressBtreeRootPrefix, PersistedSnapshotTags.AddressKeyLength,
                address.Bytes, exactMatch: true, keyFirst: false, out addressBound))
            return false;

        // Pre-fault the trailing window of the resolved bound in one syscall. The DenseByteIndex
        // trailer + hot sub-tags live at the high end of the bound; faulting from
        // <see cref="AddressBoundWarmupBytes"/> before the end gets the next sub-tag resolution's
        // pages resident in a single MADV_POPULATE_READ instead of N inline page faults.
        long warmStart = Math.Max(addressBound.Offset,
            addressBound.Offset + addressBound.Length - AddressBoundWarmupBytes);
        long warmLen = (addressBound.Offset + addressBound.Length) - warmStart;
        _reservation.TouchRangePopulate(warmStart, warmLen);
        useSpanReader = warmLen >= addressBound.Length;

        // keyFirst=false bound is (flagByteOffset - valueLength, valueLength), so the
        // entry's FlagByte offset = bound.Offset + bound.Length.
        _addrCache.Insert(address, addressBound.Offset + addressBound.Length);
        return true;
    }

    public bool TryGetAccount(Address address, out Account? account)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound, out bool useSpanReader))
        {
            account = null;
            return false;
        }
        if (useSpanReader)
        {
            using NoOpPin pin = reader.PinBuffer(addrBound);
            SpanByteReader spanReader = new(pin.Buffer);
            return TryGetAccountInner<SpanByteReader, NoOpPin>(
                in spanReader, new Bound(0, addrBound.Length), out account);
        }
        return TryGetAccountInner<ArenaByteReader, NoOpPin>(in reader, addrBound, out account);
    }

    private static bool TryGetAccountInner<TReader, TPin>(
        scoped in TReader reader, Bound addrBound, out Account? account)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!PersistedSnapshotReader.TryGetAccount<TReader, TPin>(in reader, addrBound, out Bound b))
        {
            account = null;
            return false;
        }
        int bLenInt = checked((int)b.Length);
        Span<byte> buf = bLenInt <= 256 ? stackalloc byte[256] : new byte[bLenInt];
        Span<byte> rlp = buf[..bLenInt];
        reader.TryRead(b.Offset, rlp);
        if (rlp.Length == 1 && rlp[0] == PersistedSnapshotTags.AccountDeletedMarkerByte)
        {
            account = null;
            return true;
        }
        Rlp.ValueDecoderContext ctx = new(rlp);
        account = AccountDecoder.Slim.Decode(ref ctx);
        return true;
    }

    public bool TryGetSlot(Address address, in UInt256 index, ref SlotValue slotValue)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound, out bool useSpanReader))
            return false;
        if (useSpanReader)
        {
            using NoOpPin pin = reader.PinBuffer(addrBound);
            SpanByteReader spanReader = new(pin.Buffer);
            return TryGetSlotInner<SpanByteReader, NoOpPin>(
                in spanReader, new Bound(0, addrBound.Length), in index, ref slotValue);
        }
        return TryGetSlotInner<ArenaByteReader, NoOpPin>(in reader, addrBound, in index, ref slotValue);
    }

    private static bool TryGetSlotInner<TReader, TPin>(
        scoped in TReader reader, Bound addrBound, in UInt256 index, ref SlotValue slotValue)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        if (!PersistedSnapshotReader.TryGetSlot<TReader, TPin>(in reader, addrBound, in index, out Bound b))
            return false;
        Span<byte> buf = stackalloc byte[PersistedSnapshotTags.RlpSlotValueBufferSize];
        Span<byte> raw = buf[..checked((int)b.Length)];
        reader.TryRead(b.Offset, raw);
        // length 0 = null/deleted slot (empty payload); a present value is RLP-wrapped.
        ReadOnlySpan<byte> value = raw.Length == 0 ? raw : new Rlp.ValueDecoderContext(raw).DecodeByteArraySpan();
        slotValue = SlotValue.FromSpanWithoutLeadingZero(value);
        return true;
    }

    public bool? TryGetSelfDestructFlag(Address address)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound, out bool useSpanReader))
            return null;
        if (useSpanReader)
        {
            using NoOpPin pin = reader.PinBuffer(addrBound);
            SpanByteReader spanReader = new(pin.Buffer);
            return PersistedSnapshotReader.TryGetSelfDestructFlag<SpanByteReader, NoOpPin>(
                in spanReader, new Bound(0, addrBound.Length));
        }
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
        if (!PersistedSnapshotReader.TryGetStorageTrieAddressHsstBound<ArenaByteReader, NoOpPin>(
                in reader, in addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryLoadStorageNodeRlpInBound<ArenaByteReader, NoOpPin>(
                in reader, addrBound, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveTrieRlp(bound);
        return true;
    }

    // Worst-case Merkle-Patricia branch node: 17 entries × (1-byte prefix + 32-byte hash)
    // plus a 3-byte long-list framing header ≈ 564 bytes. Round up to 568 so the read
    // covers any branch node in one pread.
    private const int MaxTrieNodeRlpBytes = 568;

    private byte[] ReadBlobArenaRlp(ushort blobArenaId, int offset)
    {
        BlobArenaFile file = _blobManager.GetFile(blobArenaId);
        Span<byte> buf = stackalloc byte[MaxTrieNodeRlpBytes];
        int bytesRead = file.RandomRead(offset, buf);
        Rlp.ValueDecoderContext ctx = new(buf[..bytesRead]);
        int totalLength = ctx.PeekNextRlpLength();
        if (totalLength > bytesRead)
            throw new InvalidDataException(
                $"Trie-node RLP at blob arena {blobArenaId}+{offset} declares {totalLength} bytes " +
                $"but only {bytesRead} were read (MaxTrieNodeRlpBytes = {MaxTrieNodeRlpBytes}).");
        byte[] result = new byte[totalLength];
        buf[..totalLength].CopyTo(result);
        return result;
    }

    /// <summary>
    /// Materialise the trie-node RLP at <paramref name="localBound"/>, which holds a
    /// <see cref="NodeRef"/> pointing at the actual RLP bytes in a blob arena.
    /// </summary>
    internal byte[] ResolveTrieRlp(Bound localBound)
    {
        NodeRef nodeRef = default;
        Span<byte> nr = MemoryMarshal.AsBytes(new Span<NodeRef>(ref nodeRef))[..checked((int)localBound.Length)];
        ArenaByteReader reader = _reservation.CreateReader();
        reader.TryRead(localBound.Offset, nr);
        return ReadBlobArenaRlp(nodeRef.BlobArenaId, nodeRef.RlpDataOffset);
    }

    internal void AdviseDontNeed() => _reservation.AdviseDontNeed();

    /// <summary>
    /// Issue <c>posix_fadvise(WILLNEED)</c> over this base snapshot's contiguous trie-RLP
    /// region so the kernel prefetches it ahead of a random-access read pass. No-op for
    /// compacted / CompactSized snapshots (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="PersistenceManager"/> before scanning a linked CompactSized: its
    /// <c>NodeRef</c>s scatter across the base snapshots' blob arenas, so bulk-prefetching
    /// each base's region turns the otherwise-random blob reads into kernel read-ahead.
    /// </remarks>
    public void AdviseWillNeedBlobRange()
    {
        if (BlobRange.IsEmpty) return;
        _blobManager.GetFile(BlobRange.BlobArenaId).FadviseWillNeed(BlobRange.Offset, BlobRange.Length);
    }

    /// <summary>
    /// Issue <c>posix_fadvise(DONTNEED)</c> over this base snapshot's contiguous trie-RLP
    /// region, dropping it from the OS page cache. No-op for compacted / CompactSized
    /// snapshots (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="AdviseWillNeedBlobRange"/>: called once the CompactSized
    /// referencing this base has been written to RocksDB, so the prefetched pages are
    /// released rather than lingering until the base snapshot is pruned.
    /// </remarks>
    public void AdviseDontNeedBlobRange()
    {
        if (BlobRange.IsEmpty) return;
        _blobManager.GetFile(BlobRange.BlobArenaId).FadviseDontNeed(BlobRange.Offset, BlobRange.Length);
    }

    public bool TryAcquire() => TryAcquireLease();

    /// <summary>
    /// Advise this snapshot's mmap range cold (<c>madvise(MADV_DONTNEED)</c> plus
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c>) and clear the per-arena page-tracker
    /// entries that cover it. Intended as a hook for callers that have superseded this
    /// snapshot but want to drop its resident pages eagerly rather than waiting for full
    /// disposal — e.g. the compactor releasing sources after merging them into a new snapshot.
    /// </summary>
    /// <remarks>
    /// Drops page-cache pages only — it does not punch a hole, because the snapshot stays
    /// alive and readable; subsequent reads simply pay a cold-page fault. Does not touch the
    /// inline address-bound cache: its 64 bytes stay on the snapshot and the cached offsets
    /// remain content-verified against the (now-cold) mmap range, so subsequent reads still
    /// hit the cache. Idempotent and safe to call from any thread.
    /// </remarks>
    public void Demote() => _reservation.AdviseAndFadviseDontNeed();

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
        // CleanUp. GetFile is a lock-free array read kept valid by that same lease.
        foreach (ushort id in GetRefIdsEnumerator())
        {
            BlobArenaFile file = _blobManager.GetFile(id);
            file.Dispose();
            // Opportunistic reclaim: if we were the last external lessee, signal the
            // manager to drop the file's frontier back to 0 so BlobAllocatedBytes
            // reflects "no live NodeRef into this file" and the file becomes packing-
            // reusable from offset 0. The manager re-validates under its own lock.
            if (file.HasOnlyManagerLease)
                _blobManager.TryResetOrphanedFrontier(file);
        }
        _reservation.Dispose();

        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -_bloom.DataBytes);
        _bloom.Dispose();

        Metrics.ActivePersistedSnapshotCount.AddBy(_label, -1);
    }
}
