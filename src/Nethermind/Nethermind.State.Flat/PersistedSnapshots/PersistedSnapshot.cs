// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by columnar HSST data on disk (or in memory).
/// The outer HSST has 5 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, state root values
///   Column 0x01: AddressHash (20 bytes, keccak256(address)[..20]) → per-address HSST {
///       0x01 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → Storage trie node RLP, path length 6-15)
///       0x02 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → Storage trie node RLP, path length 16+)
///       0x03 (SlotSubTag):            nested HSST (SlotPrefix(31) → nested ByteTagMap(SlotSuffix(1 byte) → SlotValue))
///       0x04 (AccountSubTag):         raw account slim RLP bytes (empty = deleted account)
///       0x05 (SelfDestructSubTag):    raw SD flag bytes (empty = destructed, 0x01 = new account)
///   }
///   Column 0x03: TreePath (8 bytes compact) → State trie node RLP (path length 6-15)
///   Column 0x05: TreePath (3 bytes: PathByte0, PathByte1, Length) → State trie node RLP (path length 0-5)
///   Column 0x06: TreePath.Path (32 bytes) + PathLength (1 byte) → State trie node RLP (path length 16+)
/// </summary>
public sealed class PersistedSnapshot : RefCountingDisposable
{
    // Tag prefixes for outer HSST columns
    internal static readonly byte[] MetadataTag = [0x00];
    internal static readonly byte[] AccountColumnTag = [0x01];
    internal static readonly byte[] StateNodeTag = [0x03];
    internal static readonly byte[] StateTopNodesTag = [0x05];
    internal static readonly byte[] StateNodeFallbackTag = [0x06];

    // Sub-tags within per-address HSST (sorted byte order). Storage trie nodes come
    // first so unchanged accounts keep their account/SD entries at low offsets.
    internal static readonly byte[] StorageCompactSubTag = [0x01];
    internal static readonly byte[] StorageFallbackSubTag = [0x02];
    internal static readonly byte[] SlotSubTag = [0x03];
    internal static readonly byte[] AccountSubTag = [0x04];
    internal static readonly byte[] SelfDestructSubTag = [0x05];

    // Tiny per-snapshot CLOCK cache that skips the outer-column + address-hash seek on
    // repeat lookups. The cached Bound is the per-address inner-HSST bound after seeking
    // (AccountColumnTag, addressHash[..20]). Since accounts, slots, self-destruct, and
    // both storage-trie partitions all live under that single bound, every per-address
    // path shares this cache. Bounds are stable for the lifetime of the snapshot since
    // the data is immutable; we only cache successful seeks (negative lookups go through
    // the bloom filter).
    private const int AddressBoundCacheCapacity = 8;

    private readonly ArenaReservation _reservation;
    private readonly Dictionary<int, PersistedSnapshot>? _referencedSnapshots;
    private readonly ClockCache<Hash256AsKey, Bound> _addressBoundCache = new(AddressBoundCacheCapacity);

    internal ICollection<PersistedSnapshot>? ReferencedSnapshots => _referencedSnapshots?.Values;
    internal Dictionary<int, PersistedSnapshot>? ReferencedSnapshotsLookup => _referencedSnapshots;
    internal bool HasNodeRefs { get; }

    public int Id { get; }
    public StateId From { get; }
    public StateId To { get; }
    public PersistedSnapshotType Type { get; }

    /// <summary>
    /// IDs of base snapshots referenced by NodeRefs in this compacted snapshot.
    /// Null for base snapshots or compacted snapshots with no NodeRef references.
    /// </summary>
    public int[]? ReferencedSnapshotIds { get; }

    public long Size => _reservation.Size;

    internal ArenaReservation Reservation => _reservation;

    /// <summary>
    /// Begin a scoped whole-buffer read over this snapshot's reservation. Forwards to
    /// <see cref="ArenaReservation.BeginWholeReadSession"/>.
    /// </summary>
    public WholeReadSession BeginWholeReadSession() => _reservation.BeginWholeReadSession();

    /// <summary>
    /// Construct a reader over this snapshot's bytes. Delegates to
    /// <see cref="ArenaReservation.CreateReader"/> so the storage layer owns the
    /// reader-construction policy.
    /// </summary>
    internal ArenaByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Materialise the value at <paramref name="localBound"/> in this snapshot's bytes,
    /// dereferencing across snapshots when this snapshot stores NodeRefs. Reads via the
    /// reader abstraction (no GetSpan), copying directly into a heap-allocated byte[].
    /// </summary>
    internal byte[] ResolveValueAt(Bound localBound)
    {
        ArenaByteReader reader = _reservation.CreateReader();
        if (!HasNodeRefs || _referencedSnapshots is null)
        {
            byte[] result = new byte[localBound.Length];
            reader.TryRead(localBound.Offset, result);
            return result;
        }

        Span<byte> nrBuf = stackalloc byte[NodeRef.Size];
        Span<byte> nr = nrBuf[..checked((int)localBound.Length)];
        reader.TryRead(localBound.Offset, nr);
        NodeRef nodeRef = NodeRef.Read(nr);
        if (!_referencedSnapshots.TryGetValue(nodeRef.SnapshotId, out PersistedSnapshot? snap))
            throw new InvalidOperationException($"Referenced snapshot {nodeRef.SnapshotId} not found");
        return snap.ReadRlpItem(nodeRef.RlpDataOffset);
    }

    public PersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, ArenaReservation reservation,
        PersistedSnapshot[]? referencedSnapshots = null)
    {
        Id = id;
        From = from;
        To = to;
        Type = type;
        _reservation = reservation;
        _reservation.AcquireLease();
        ArenaByteReader bootReader = CreateReader();
        HasNodeRefs = PersistedSnapshotReader.CheckHasNodeRefsFlag<ArenaByteReader, NoOpPin>(in bootReader);

        if (referencedSnapshots is { Length: > 0 })
        {
            _referencedSnapshots = new Dictionary<int, PersistedSnapshot>(referencedSnapshots.Length);
            ReferencedSnapshotIds = new int[referencedSnapshots.Length];
            for (int i = 0; i < referencedSnapshots.Length; i++)
            {
                referencedSnapshots[i].TryAcquireLease();
                ReferencedSnapshotIds[i] = referencedSnapshots[i].Id;
                _referencedSnapshots[referencedSnapshots[i].Id] = referencedSnapshots[i];
            }
        }
    }

    /// <summary>
    /// Resolve the per-address inner-HSST bound, hitting the address-hash LRU first so
    /// repeat lookups for the same address-hash skip the outer column-tag + 20-byte
    /// address-hash seeks. The same bound serves account / slot / self-destruct / storage
    /// trie sub-tags. Returns false (with default <paramref name="addressBound"/>) when
    /// the address-hash is not present in this snapshot.
    /// </summary>
    private bool TryGetAddressBound(in ArenaByteReader reader, Hash256 addressHash, out Bound addressBound)
    {
        if (_addressBoundCache.TryGet(addressHash, out addressBound))
            return true;
        if (!PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, addressHash, out addressBound))
            return false;
        _addressBoundCache.Set(addressHash, addressBound);
        return true;
    }

    public bool TryGetAccount(PersistedSnapshotBloom bloom, Hash256 addressHash, out Account? account)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(addressHash)))
        {
            account = null;
            return false;
        }
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetAccount<ArenaByteReader, NoOpPin>(in reader, addrBound, out Bound b))
        {
            account = null;
            return false;
        }
        // Presence-marker encoding: PersistedSnapshotReader.TryGetAccount filters out
        // length-0 (absent) entries; a present entry is either [0x00] = deleted or
        // RLP-bytes = present. Slim account RLP starts with a list header (0xc0+) so
        // the 0x00 marker never collides with a valid RLP first byte.
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

    public bool TryGetSlot(PersistedSnapshotBloom bloom, Hash256 addressHash, in UInt256 index, ref SlotValue slotValue)
    {
        ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(addressHash);
        if (!bloom.KeyBloom.MightContain(addrKey) || !bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.SlotKey(addrKey, in index)))
            return false;
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetSlot<ArenaByteReader, NoOpPin>(in reader, addrBound, in index, out Bound b))
            return false;
        Span<byte> buf = stackalloc byte[32];
        Span<byte> raw = buf[..checked((int)b.Length)];
        reader.TryRead(b.Offset, raw);
        slotValue = SlotValue.FromSpanWithoutLeadingZero(raw);
        return true;
    }

    public bool IsSelfDestructed(PersistedSnapshotBloom bloom, Hash256 addressHash)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(addressHash)))
            return false;
        ArenaByteReader reader = CreateReader();
        return TryGetAddressBound(in reader, addressHash, out Bound addrBound)
            && PersistedSnapshotReader.IsSelfDestructed<ArenaByteReader, NoOpPin>(in reader, addrBound);
    }

    /// <summary>
    /// Get the self-destruct flag with boolean distinction.
    /// Returns null if no self-destruct entry exists for this address-hash.
    /// Returns true if this is a new account (value = 0x01), false if destructed (value = empty).
    /// </summary>
    public bool? TryGetSelfDestructFlag(PersistedSnapshotBloom bloom, Hash256 addressHash)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(addressHash)))
            return null;
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, addressHash, out Bound addrBound))
            return null;
        return PersistedSnapshotReader.TryGetSelfDestructFlag<ArenaByteReader, NoOpPin>(in reader, addrBound);
    }

    public bool TryLoadStateNodeRlp(PersistedSnapshotBloom bloom, scoped in TreePath path, out byte[]? nodeRlp)
    {
        if (!bloom.TrieBloom.MightContain(PersistedSnapshotBloomBuilder.StatePathKey(in path)))
        {
            nodeRlp = null;
            return false;
        }
        ArenaByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryLoadStateNodeRlp<ArenaByteReader, NoOpPin>(in reader, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveValueAt(bound);
        return true;
    }

    public bool TryLoadStorageNodeRlp(PersistedSnapshotBloom bloom, Hash256 addressHash, in TreePath path, out byte[]? nodeRlp)
    {
        if (!bloom.TrieBloom.MightContain(PersistedSnapshotBloomBuilder.StorageNodeKey(addressHash, in path)))
        {
            nodeRlp = null;
            return false;
        }
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, addressHash, out Bound addrBound) ||
            !PersistedSnapshotReader.TryLoadStorageNodeRlpInBound<ArenaByteReader, NoOpPin>(in reader, addrBound, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveValueAt(bound);
        return true;
    }

    /// <summary>
    /// Read the "ref_ids" list from a snapshot's metadata column.
    /// Returns null if the metadata or "ref_ids" key is missing.
    /// </summary>
    public static int[]? ReadRefIdsFromMetadata(ReadOnlySpan<byte> snapshotData)
    {
        SpanByteReader reader = new(snapshotData);
        return PersistedSnapshotReader.ReadRefIdsFromMetadata<SpanByteReader, NoOpPin>(in reader);
    }

    /// <summary>
    /// Reader-based <see cref="ReadRefIdsFromMetadata(ReadOnlySpan{byte})"/>. Avoids the
    /// caller having to materialise a whole-reservation span, so it works with
    /// chunk-aware readers once those land.
    /// </summary>
    public static int[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct =>
        PersistedSnapshotReader.ReadRefIdsFromMetadata<TReader, TPin>(in reader);

    /// <summary>
    /// Read a self-describing RLP item starting at <paramref name="rlpDataOffset"/>. Peeks the
    /// RLP header (≤ 9 bytes) to recover the total item length via
    /// <see cref="Rlp.ValueDecoderContext.PeekNextRlpLength"/>, then copies the full item
    /// into a heap-allocated array. Used to deref <see cref="NodeRef"/> values, which now
    /// point directly at the RLP rather than at a per-entry length-metadata cursor.
    /// </summary>
    public byte[] ReadRlpItem(int rlpDataOffset)
    {
        ArenaByteReader reader = _reservation.CreateReader();
        // Worst-case RLP prefix is 1 + 8 bytes (long form with 8-byte length). Clamp the
        // peek to the remaining reservation so an item near the end of the buffer doesn't
        // trip TryRead's bounds check; PeekNextRlpLength only consumes as many prefix bytes
        // as the prefix actually requires.
        Span<byte> headerBuf = stackalloc byte[9];
        long remaining = reader.Length - rlpDataOffset;
        Span<byte> header = headerBuf[..(int)Math.Min(headerBuf.Length, remaining)];
        reader.TryRead(rlpDataOffset, header);
        Rlp.ValueDecoderContext ctx = new(header);
        int totalLength = ctx.PeekNextRlpLength();
        byte[] result = new byte[totalLength];
        reader.TryRead(rlpDataOffset, result);
        return result;
    }

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    public bool TryAcquire() => TryAcquireLease();

    protected override void CleanUp()
    {
        _reservation.Dispose();
        if (_referencedSnapshots is not null)
        {
            foreach (PersistedSnapshot snapshot in _referencedSnapshots.Values)
                snapshot.Dispose();
        }
    }
}
