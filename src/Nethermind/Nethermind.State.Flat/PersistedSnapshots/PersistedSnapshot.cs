// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by a single-level <see cref="SortedTable"/> on disk. Trie-node RLP
/// values are not stored inline — every trie-node entry holds a <see cref="NodeRef"/> pointing into
/// a blob arena. The reservation owned by this snapshot stores the metadata table bytes only.
/// </summary>
/// <remarks>
/// On-disk vocabulary (column / subcolumn tags, metadata keys, value markers) is defined in
/// <see cref="PersistedSnapshotTags"/> and materialized by <see cref="PersistedSnapshotKey"/>.
/// Every lookup binary searches the whole table — there is no per-address index or bound cache.
/// </remarks>
public sealed class PersistedSnapshot : SmallRefCountingDisposable
{
    private readonly ArenaReservation _reservation;
    // Metric label (tier + compact size) for the per-(tier, size) ActivePersistedSnapshotCount gauge.
    private readonly PersistedSnapshotLabel _label;
    // Each id is resolved on demand via _blobManager.GetFile(id), a lock-free O(1) array read. The
    // canonical leased-id list lives on disk in this snapshot's metadata under the "ref_ids" key.
    private readonly BlobArenaManager _blobManager;

    public StateId From { get; }
    public StateId To { get; }

    /// <summary>The persisted tier (bucket) this snapshot belongs to.</summary>
    internal SnapshotTier Tier { get; }

    // Unified bloom gating all reads of this snapshot (address / slot / self-destruct keys and
    // state- / storage-trie paths in one filter), held through a ref-counted owner so a large
    // compaction can share one filter across the snapshots it contains.
    private readonly RefCountedBloomFilter _bloom;
    public BloomFilter Bloom => _bloom.Filter;

    /// <summary>The ref-counted bloom owner, for re-registering a twin over this snapshot that shares
    /// another snapshot's bloom (the twin adopts a lease on that owner).</summary>
    internal RefCountedBloomFilter BloomRef => _bloom;

    /// <summary>
    /// The contiguous trie-RLP region this snapshot occupies in its blob arena, used to prefetch
    /// the whole region in one bulk read-ahead. Non-empty only for base snapshots (which write all
    /// their RLPs through one <see cref="BlobArenaWriter"/>); <see cref="BlobRange.None"/> for
    /// compacted / CompactSized snapshots, whose <c>NodeRef</c>s scatter across many blob arenas.
    /// </summary>
    public BlobRange BlobRange { get; }

    public long Size => _reservation.Size;

    internal ArenaReservation Reservation => _reservation;

    /// <summary>
    /// Begin a scoped whole-buffer read over this snapshot's reservation. By default the
    /// session madvises the mmap range cold on dispose.
    /// </summary>
    public WholeReadSession BeginWholeReadSession(bool adviseDontNeedOnDispose = true) =>
        _reservation.BeginWholeReadSession(adviseDontNeedOnDispose);

    private ArenaByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Construct a snapshot over a pre-leased metadata reservation. The caller MUST have already
    /// acquired one lease per blob arena id referenced by the snapshot's <c>ref_ids</c> metadata,
    /// and is responsible for rolling those leases back on construction failure. This ctor bumps the
    /// metadata reservation lease and stashes the manager ref for later id → file resolution.
    /// </summary>
    public PersistedSnapshot(StateId from, StateId to, ArenaReservation reservation,
        BlobArenaManager blobManager, SnapshotTier tier, RefCountedBloomFilter bloom)
    {
        From = from;
        To = to;
        Tier = tier;
        _reservation = reservation;
        _label = new PersistedSnapshotLabel(tier.MetricTierLabel(), to.BlockNumber - from.BlockNumber);
        _blobManager = blobManager;
        _bloom = bloom;
        _reservation.AcquireLease();

        // Walk the on-disk ref_ids stream once and lease each referenced blob arena file. On
        // partial failure we walk the prefix already acquired and drop those leases before
        // unwinding the metadata reservation's lease and rethrowing.
        int acquired = 0;
        try
        {
            ArenaByteReader metaReader = _reservation.CreateReader();
            BlobRange = ReadBlobRange(in metaReader, new Bound(0, metaReader.Length));

            RefIdsEnumerator<ArenaByteReader, NoOpPin> e = GetRefIdsEnumerator();
            while (e.MoveNext())
            {
                if (!_blobManager.TryLeaseFile(e.Current, out _))
                    throw new InvalidOperationException($"Blob arena {e.Current} not registered with the blob manager");
                acquired++;
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
            _bloom.Dispose();
            _reservation.Dispose();
            throw;
        }

        // Increment only after every throw path above has been cleared, so a
        // partial-construction failure does not leave the gauge off by one.
        Metrics.ActivePersistedSnapshotCount.AddBy(_label, 1);
    }

    /// <summary>Seek a metadata entry (column <c>0xFF</c>) by its NUL-padded name and return its
    /// value bound, or a default bound if absent.</summary>
    private static Bound SeekMetadata<TReader, TPin>(scoped in TReader reader, Bound table, scoped ReadOnlySpan<byte> name)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> key = stackalloc byte[1 + PersistedSnapshotTags.MetadataKeyLength];
        int len = PersistedSnapshotKey.WriteMetadataKey(key, name);
        return SortedTableReader.TrySeek<TReader, TPin>(in reader, table, key[..len], out Bound b) ? b : default;
    }

    /// <summary>
    /// Forward iterator over this snapshot's referenced blob arena ids, reading the ref_ids value a
    /// little-endian ushort at a time. Backed by a plain <see cref="ArenaByteReader"/> — the
    /// surrounding snapshot's lease keeps the mmap alive.
    /// </summary>
    private RefIdsEnumerator<ArenaByteReader, NoOpPin> GetRefIdsEnumerator()
    {
        ArenaByteReader reader = _reservation.CreateReader();
        Bound refIds = SeekMetadata<ArenaByteReader, NoOpPin>(in reader, new Bound(0, reader.Length), PersistedSnapshotTags.MetadataRefIdsKey);
        return new RefIdsEnumerator<ArenaByteReader, NoOpPin>(reader, refIds);
    }

    /// <summary>
    /// Read the <c>blob_range</c> metadata entry — the contiguous trie-RLP run recorded by base
    /// snapshots. Returns <see cref="BlobRange.None"/> when the key is absent (compacted /
    /// CompactSized snapshots) or malformed.
    /// </summary>
    private static BlobRange ReadBlobRange(scoped in ArenaByteReader reader, Bound table)
    {
        Bound b = SeekMetadata<ArenaByteReader, NoOpPin>(in reader, table, PersistedSnapshotTags.MetadataBlobRangeKey);
        if (b.Length == BlobRange.SerializedSize)
        {
            BlobRange range = default;
            if (reader.TryRead(b.Offset, MemoryMarshal.AsBytes(new Span<BlobRange>(ref range))))
                return range;
        }
        return BlobRange.None;
    }

    /// <summary>
    /// Ref-struct enumerator backing <see cref="GetRefIdsEnumerator"/>. Yields each
    /// <see cref="NodeRef.BlobArenaId"/> stored in the snapshot's <c>ref_ids</c> metadata entry in
    /// ascending order without allocating a <c>ushort[]</c>.
    /// </summary>
    private ref struct RefIdsEnumerator<TReader, TPin>
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TPin : struct, IBufferPin, allows ref struct
    {
        private TReader _reader;
        private long _cursor;
        private long _end;
        private ushort _current;

        internal RefIdsEnumerator(TReader reader, Bound refIdsBound)
        {
            _reader = reader;
            if (refIdsBound.Length > 0 && refIdsBound.Length % 2 == 0)
            {
                _cursor = refIdsBound.Offset;
                _end = refIdsBound.Offset + refIdsBound.Length;
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

    public bool TryGetAccount(Address address, out Account? account)
    {
        ArenaByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryGetAccount<ArenaByteReader, NoOpPin>(
                in reader, new Bound(0, reader.Length), address, out Bound b))
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
        if (!PersistedSnapshotReader.TryGetSlot<ArenaByteReader, NoOpPin>(
                in reader, new Bound(0, reader.Length), address, in index, out Bound b))
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
        return PersistedSnapshotReader.TryGetSelfDestructFlag<ArenaByteReader, NoOpPin>(
            in reader, new Bound(0, reader.Length), address);
    }

    public bool TryLoadStateNodeRlp(scoped in TreePath path, out byte[]? nodeRlp)
    {
        ArenaByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryLoadStateNodeRlp<ArenaByteReader, NoOpPin>(
                in reader, new Bound(0, reader.Length), in path, out Bound bound))
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
        if (!PersistedSnapshotReader.TryLoadStorageNodeRlp<ArenaByteReader, NoOpPin>(
                in reader, new Bound(0, reader.Length), in addressHash, in path, out Bound bound))
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
    /// Issue <c>posix_fadvise(WILLNEED)</c> over this base snapshot's contiguous trie-RLP region so
    /// the kernel prefetches it ahead of a random-access read pass. No-op for compacted / CompactSized
    /// snapshots (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    public void AdviseWillNeedBlobRange()
    {
        if (BlobRange.IsEmpty) return;
        _blobManager.GetFile(BlobRange.BlobArenaId).FadviseWillNeed(BlobRange.Offset, BlobRange.Length);
    }

    /// <summary>
    /// Issue <c>posix_fadvise(DONTNEED)</c> over this base snapshot's contiguous trie-RLP region,
    /// dropping it from the OS page cache. No-op for compacted / CompactSized snapshots
    /// (<see cref="BlobRange.None"/>) or empty regions.
    /// </summary>
    public void AdviseDontNeedBlobRange()
    {
        if (BlobRange.IsEmpty) return;
        _blobManager.GetFile(BlobRange.BlobArenaId).FadviseDontNeed(BlobRange.Offset, BlobRange.Length);
    }

    public bool TryAcquire() => TryAcquireLease();

    /// <summary>
    /// Advise this snapshot's mmap range cold and clear the per-arena page-tracker entries that
    /// cover it. A hook for callers that have superseded this snapshot but want to drop its resident
    /// pages eagerly rather than waiting for full disposal.
    /// </summary>
    public void Demote() => _reservation.AdviseAndFadviseDontNeed();

    /// <summary>
    /// Mark every file this snapshot references (its metadata <see cref="ArenaReservation"/>'s
    /// <see cref="ArenaFile"/> and every leased <see cref="BlobArenaFile"/>) for
    /// shutdown-preservation. Reads the leased id list from the metadata on each call; idempotent
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
        // Drain the iterator before disposing the reservation — the iterator reads through the
        // reservation's mmap via an ArenaByteReader, and this snapshot's own lease (acquired at
        // construction) keeps the mmap alive until it drops at the end of CleanUp.
        foreach (ushort id in GetRefIdsEnumerator())
        {
            BlobArenaFile file = _blobManager.GetFile(id);
            file.Dispose();
            // Opportunistic reclaim: if we were the last external lessee, signal the manager to
            // drop the file's frontier back to 0.
            if (file.HasOnlyManagerLease)
                _blobManager.TryResetOrphanedFrontier(file);
        }
        _reservation.Dispose();

        _bloom.Dispose();

        Metrics.ActivePersistedSnapshotCount.AddBy(_label, -1);
    }
}
