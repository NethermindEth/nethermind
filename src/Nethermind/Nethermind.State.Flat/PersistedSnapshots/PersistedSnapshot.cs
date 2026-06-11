// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
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
public sealed class PersistedSnapshot : RefCountingDisposable
{

    // On address-bound cache miss, pre-fault the trailing slice of the per-address inner HSST
    // in one madvise(MADV_POPULATE_READ) syscall over a fixed window at the tail of the bound.
    // The DenseByteIndex layout streams values in descending-tag order, so the hot small-blob
    // sub-tags (AccountSubTag, SelfDestructSubTag) and the index trailer cluster at the tail —
    // 32 KiB lands at most 8 pages and covers every realistic hot inner HSST entirely. When the
    // whole bound fits inside the window, the sub-tag walk continues over the now-resident span
    // through a zero-touch <see cref="SpanByteReader"/> instead of <see cref="ArenaByteReader"/>,
    // skipping the per-read tracker probe loop for the rest of the lookup.
    private const long AddressBoundWarmupBytes = 32 * 1024;

    private AddressBoundCache _addrCache;

    // Cached descriptor of the outer address-column BTree's root, snapshotted once at
    // construction. The address column is immutable for the life of the snapshot, so the
    // values the BTree walker would otherwise read out of the trailer (root prefix bytes,
    // root size, key length) are fixed too. Caching them lets the cache-miss path of
    // <see cref="TryGetAddressBound"/> skip the two trailer-region reads in
    // <see cref="HsstBTreeReader.TrySeek"/> and start the walk from the cached root offset.
    // _addressBtreeBound.Length == 0 is the sentinel for "no address column in this snapshot"
    // (legitimate for a snapshot that touched no accounts); the miss path short-circuits to
    // "no entry" without bothering with the BTree at all.
    private readonly Bound _addressBtreeBound;
    private readonly long _addressBtreeRootStart;
    private readonly long _addressBtreeScopeEnd;
    private readonly byte[] _addressBtreeRootPrefix = [];

    private readonly ArenaReservation _reservation;
    // Manager that owns the per-id blob arena slots. The repository acquires one lease per
    // referenced id before this ctor runs and releases them in CleanUp / PersistOnShutdown,
    // resolving each id via _blobManager.GetFile(id) (lock-free O(1) array read). The
    // canonical list of leased ids lives on disk inside this snapshot's metadata HSST under
    // the "ref_ids" key — no in-memory dict.
    private readonly BlobArenaManager _blobManager;

    public StateId From { get; }
    public StateId To { get; }

    // The unified bloom gating reads of this snapshot — covers address / slot / self-destruct
    // keys plus state-trie and storage-trie paths in one filter. Owned by this snapshot: the
    // lease that keeps the snapshot alive keeps its bloom alive, and CleanUp disposes it.
    // Defaults to the AlwaysTrue sentinel (no filtering, never a false negative) for snapshots
    // created before their real bloom is available — base/compacted snapshots get their filter
    // at convert / merge time, and reload populates it via SetBloom once every snapshot is in
    // place. The query path probes Bloom.MightContain before paying for any disk read.
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
    /// The contiguous trie-RLP region this snapshot occupies in its blob arena. Non-empty
    /// only for base snapshots (which write all their RLPs through one
    /// <see cref="BlobArenaWriter"/>); <see cref="BlobRange.None"/> for compacted /
    /// persistable snapshots, whose <c>NodeRef</c>s scatter across many blob arenas.
    /// </summary>
    /// <remarks>
    /// Read once at construction from this snapshot's own metadata HSST (the
    /// <c>blob_range</c> key in column 0x00), the same way the leased <c>ref_ids</c> are
    /// walked. A snapshot whose metadata carries no <c>blob_range</c> key resolves to
    /// <see cref="BlobRange.None"/>.
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

    /// <summary>
    /// Construct a reader over this snapshot's bytes.
    /// </summary>
    internal ArenaByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Construct a snapshot over a pre-leased metadata reservation. The caller (typically
    /// <see cref="PersistedSnapshotRepository"/>) MUST have already acquired one lease per
    /// blob arena id referenced by the snapshot's <c>ref_ids</c> metadata via
    /// <see cref="BlobArenaManager.TryLeaseFile"/>, and is responsible for rolling those
    /// leases back on construction failure. This ctor just bumps the metadata reservation
    /// lease and stashes the manager ref for later id → file resolution.
    /// </summary>
    /// <param name="bloom">The unified bloom this snapshot takes ownership of, disposed with
    /// the snapshot. <c>null</c> installs the AlwaysTrue sentinel — correct (no false
    /// negatives) but unfiltered — for callers that populate the real bloom later via
    /// <see cref="SetBloom"/>.</param>
    public PersistedSnapshot(StateId from, StateId to, ArenaReservation reservation,
        BlobArenaManager blobManager, BloomFilter? bloom = null)
    {
        From = from;
        To = to;
        _reservation = reservation;
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
            // Read this snapshot's contiguous blob run from its own metadata HSST. Absent on
            // compacted / persistable snapshots, which resolve to BlobRange.None.
            BlobRange = ReadBlobRange();

            RefIdsEnumerator e = GetRefIdsEnumerator();
            while (e.MoveNext())
            {
                if (!_blobManager.TryLeaseFile(e.Current, out _))
                    throw new InvalidOperationException($"Blob arena {e.Current} not registered with the blob manager");
                acquired++;
            }

            // Cache the address-column BTree's root descriptor so the cache-miss path of
            // TryGetAddressBound can walk the tree directly without re-reading the trailer
            // and root prefix on every miss. Defensive: a missing address column (legitimate
            // for snapshots that touched no accounts) or an unreadable trailer leaves the
            // cache empty and the miss path short-circuits to "no entry" — same outcome as
            // the slow path delivered before.
            ArenaByteReader probeReader = _reservation.CreateReader();
            if (PersistedSnapshotReader.TryGetAddressColumnBound<ArenaByteReader, NoOpPin>(
                    in probeReader, out Bound addrColBound) &&
                addrColBound.Length >= 5 + 12)
            {
                Span<byte> tailBuf = stackalloc byte[5];
                if (probeReader.TryRead(addrColBound.Offset + addrColBound.Length - 5, tailBuf))
                {
                    int rootPrefixLen = tailBuf[0];
                    int rootSize = tailBuf[1] | (tailBuf[2] << 8);
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
                        _addressBtreeScopeEnd = addrColBound.Offset + addrColBound.Length - trailerLen;
                        _addressBtreeRootPrefix = rootPrefix;
                    }
                }
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
            Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -_bloom.DataBytes);
            _bloom.Dispose();
            _reservation.Dispose();
            throw;
        }

        // Increment only after every throw path above has been cleared, so a
        // partial-construction failure does not leave the gauge off by one.
        Interlocked.Increment(ref Metrics._activePersistedSnapshotCount);
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
    private RefIdsEnumerator GetRefIdsEnumerator() => new(this);

    /// <summary>
    /// Read the <c>blob_range</c> metadata entry (column 0x00) — the contiguous trie-RLP run
    /// recorded by base snapshots. Returns <see cref="BlobRange.None"/> when the key is absent
    /// (compacted / persistable snapshots) or malformed.
    /// </summary>
    private BlobRange ReadBlobRange()
    {
        ArenaByteReader reader = _reservation.CreateReader();
        HsstReader<ArenaByteReader, NoOpPin> root = new(in reader, new Bound(0, reader.Length));
        if (root.TrySeek(PersistedSnapshotTags.MetadataTag, out _) &&
            root.TrySeek(PersistedSnapshotTags.MetadataBlobRangeKey, out Bound b) &&
            b.Length == BlobRange.SerializedSize)
        {
            Span<byte> buf = stackalloc byte[BlobRange.SerializedSize];
            if (reader.TryRead(b.Offset, buf))
                return BlobRange.Read(buf);
        }
        return BlobRange.None;
    }

    /// <summary>
    /// Ref-struct enumerator backing <see cref="GetRefIdsEnumerator"/>. Yields each
    /// <see cref="NodeRef.BlobArenaId"/> stored in the snapshot's <c>ref_ids</c>
    /// metadata entry in ascending order without allocating a <c>ushort[]</c>.
    /// </summary>
    private ref struct RefIdsEnumerator
    {
        private ArenaByteReader _reader;
        private long _cursor;
        private long _end;
        private ushort _current;

        internal RefIdsEnumerator(PersistedSnapshot snapshot)
        {
            _reader = snapshot._reservation.CreateReader();
            HsstReader<ArenaByteReader, NoOpPin> root = new(in _reader, new Bound(0, _reader.Length));
            if (root.TrySeek(PersistedSnapshotTags.MetadataTag, out _) &&
                root.TrySeek(PersistedSnapshotTags.MetadataRefIdsKey, out Bound rb) &&
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
                in reader, _addressBtreeBound, _addressBtreeRootStart, _addressBtreeScopeEnd,
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

    /// <summary>
    /// Single 8-way set-associative clock (second-chance) address-bound cache, mirroring
    /// <see cref="PageResidencyTracker"/>'s hot/miss-path split. One set ⇒ 8 ways × 8 bytes
    /// = 64 bytes stored inline as a <see cref="Vector512{T}"/> field — no separate heap
    /// allocation. The runtime gives <see cref="Vector512{T}"/> its natural 64-byte alignment for
    /// the field offset, matching the single-cache-line layout the previous
    /// <see cref="NativeMemory.AlignedAlloc(nuint,nuint)"/>-based variant relied on. The
    /// <see cref="Vector512{T}"/> is never used as a SIMD vector — it is purely an
    /// alignment-bearing 64-byte storage cell, reinterpreted as <c>Span&lt;long&gt;</c> via
    /// <see cref="MemoryMarshal.CreateSpan{T}(ref T,int)"/>.
    /// </summary>
    /// <remarks>
    /// Each slot packs:
    /// <list type="bullet">
    ///   <item>bit 63: REF — armed on every hit and insert, cleared by the clock hand on a miss-pass.</item>
    ///   <item>bit 62: VALID — distinguishes an empty (0L) slot from a stored (tag=0, offset=0) entry.</item>
    ///   <item>bits 46..61: 16-bit tag (bytes 4..6 of the raw Address).</item>
    ///   <item>bits 0..45: 46-bit absolute offset of the entry's FlagByte in the outer column 0x01
    ///         entry. 46 bits = 64 TiB, ample for any real snapshot.</item>
    /// </list>
    /// keyFirst=false BTree entry shape is [Value][FlagByte][LEB128][FullKey]; on a tag match the
    /// FlagByte, LEB128 (≤ 6 bytes) and 20-byte stored raw Address are read and compared to the
    /// lookup Address to catch tag collisions / layout drift. The cached Bound is
    /// (flagByteOffset - valueLength, valueLength). Must be accessed only as an in-place field —
    /// the lock-free scans and the per-cache spin-lock operate on the storage by ref.
    /// </remarks>
    private struct AddressBoundCache
    {
        private const long RefBit = unchecked((long)0x8000_0000_0000_0000UL);
        private const long ValidBit = 0x4000_0000_0000_0000L;
        private const long KeyMask = ~RefBit;
        private const long OffsetMask = (1L << 46) - 1;
        private const int TagShift = 46;
        private const int Ways = 8;
        private const int WayMask = Ways - 1;
        private const int MetaLockBit = 1 << 7;
        private const int MetaHandMask = 0x7;
        // FlagByte (1) + LEB128 value-length (≤ 6) + raw Address (20).
        private const int ProbeBytes = 1 + 6 + PersistedSnapshotTags.AddressKeyLength;

        private Vector512<long> _slots;
        private int _meta;

        /// <summary>
        /// Hot-path lookup: lock-free 8-way scan. A tag match is a candidate, verified against the
        /// 20-byte stored raw Address on disk via <paramref name="reader"/> to filter the
        /// inevitable collisions; the matching slot's REF bit is re-armed before returning.
        /// </summary>
        public bool TryGet(in ArenaByteReader reader, Address address, out Bound bound)
        {
            Span<long> slots = MemoryMarshal.CreateSpan(
                ref Unsafe.As<Vector512<long>, long>(ref _slots), Ways);
            ushort hashTag = MemoryMarshal.Read<ushort>(address.Bytes.Slice(4, 2));
            for (int w = 0; w < Ways; w++)
            {
                long s = Volatile.Read(ref slots[w]);
                if ((s & ValidBit) == 0) continue;
                if ((ushort)((s >>> TagShift) & 0xFFFF) != hashTag) continue;

                long flagOffset = s & OffsetMask;
                Span<byte> probe = stackalloc byte[ProbeBytes];
                if (!reader.TryRead(flagOffset, probe)) continue;
                // probe[0] is the entry's FlagByte; the LEB128 value-length starts at probe[1].
                int pos = 1;
                long valueLength = Leb128.Read(probe, ref pos);
                if (!probe.Slice(pos, PersistedSnapshotTags.AddressKeyLength)
                        .SequenceEqual(address.Bytes))
                    continue;

                if ((s & RefBit) == 0)
                    Interlocked.Or(ref slots[w], RefBit);
                bound = new Bound(flagOffset - valueLength, valueLength);
                return true;
            }
            bound = default;
            return false;
        }

        /// <summary>
        /// Miss-path insert of the entry whose FlagByte sits at <paramref name="flagByteOffset"/>.
        /// Takes the per-cache spin-lock, then re-scans for an existing matching entry, an empty
        /// way, and finally the clock victim.
        /// </summary>
        public void Insert(Address address, long flagByteOffset)
        {
            ushort hashTag = MemoryMarshal.Read<ushort>(address.Bytes.Slice(4, 2));
            long newEntry = ValidBit
                          | RefBit
                          | ((long)hashTag << TagShift)
                          | (flagByteOffset & OffsetMask);

            ref int meta = ref _meta;
            AcquireLock(ref meta);
            try
            {
                Span<long> slots = MemoryMarshal.CreateSpan(
                    ref Unsafe.As<Vector512<long>, long>(ref _slots), Ways);
                // Re-scan under the lock — another miss-path racer may already have installed
                // this exact (tag, offset) pair, in which case just re-arm its REF bit.
                for (int w = 0; w < Ways; w++)
                {
                    long s = slots[w];
                    if ((s & KeyMask) == (newEntry & KeyMask))
                    {
                        Volatile.Write(ref slots[w], s | RefBit);
                        return;
                    }
                }

                // Look for an empty way (VALID=0). New arrivals already carry REF=1 so they
                // survive the first clock pass.
                for (int w = 0; w < Ways; w++)
                {
                    if (slots[w] == 0L)
                    {
                        Volatile.Write(ref slots[w], newEntry);
                        return;
                    }
                }

                // Set is full — run the clock. Worst case: 8 set-REFs ⇒ one full pass clears
                // them, the second pass finds an unreferenced way. Bound at 2*Ways iterations.
                int hand = meta & MetaHandMask;
                for (int i = 0; i < 2 * Ways; i++)
                {
                    long s = slots[hand];
                    if ((s & RefBit) != 0)
                    {
                        Volatile.Write(ref slots[hand], s & ~RefBit);
                        hand = (hand + 1) & WayMask;
                        continue;
                    }

                    Volatile.Write(ref slots[hand], newEntry);
                    hand = (hand + 1) & WayMask;
                    meta = (meta & ~MetaHandMask) | hand;
                    return;
                }

                Debug.Fail("Clock scan failed to find a victim");
            }
            finally
            {
                ReleaseLock(ref meta);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AcquireLock(ref int meta)
        {
            SpinWait spinner = default;
            while (true)
            {
                int observed = Volatile.Read(ref meta);
                if ((observed & MetaLockBit) == 0)
                {
                    int withLock = observed | MetaLockBit;
                    if (Interlocked.CompareExchange(ref meta, withLock, observed) == observed)
                        return;
                }
                spinner.SpinOnce();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseLock(ref int meta) =>
            Volatile.Write(ref meta, meta & ~MetaLockBit);
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
            using NoOpPin pin = reader.PinBuffer(addrBound.Offset, addrBound.Length);
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
            using NoOpPin pin = reader.PinBuffer(addrBound.Offset, addrBound.Length);
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
        Span<byte> buf = stackalloc byte[32];
        Span<byte> raw = buf[..checked((int)b.Length)];
        reader.TryRead(b.Offset, raw);
        slotValue = SlotValue.FromSpanWithoutLeadingZero(raw);
        return true;
    }

    public bool? TryGetSelfDestructFlag(Address address)
    {
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound, out bool useSpanReader))
            return null;
        if (useSpanReader)
        {
            using NoOpPin pin = reader.PinBuffer(addrBound.Offset, addrBound.Length);
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

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    /// <summary>
    /// Issue <c>posix_fadvise(WILLNEED)</c> over this base snapshot's contiguous trie-RLP
    /// region so the kernel prefetches it ahead of a random-access read pass. No-op for
    /// compacted / persistable snapshots (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="PersistenceManager"/> before scanning a linked persistable: its
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
    /// region, dropping it from the OS page cache. No-op for compacted / persistable
    /// snapshots (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="AdviseWillNeedBlobRange"/>: called once the persistable
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
        // CleanUp. GetFile is a lock-free array read; the lease we acquired at construction
        // kept the slot alive until now.
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

        Interlocked.Decrement(ref Metrics._activePersistedSnapshotCount);
    }
}
