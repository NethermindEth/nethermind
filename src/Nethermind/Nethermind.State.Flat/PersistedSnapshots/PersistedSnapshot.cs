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
/// The outer HSST has 7 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, state root values
///   Column 0x01: Address (20 bytes) → per-address HSST {
///       0x01 (SlotSubTag):         nested HSST (SlotPrefix(31) → nested ByteTagMap(SlotSuffix(1 byte) → SlotValue))
///       0x02 (SelfDestructSubTag): raw SD flag bytes (empty = destructed, 0x01 = new account)
///       0x03 (AccountSubTag):      raw account slim RLP bytes (empty = deleted account)
///   }
///   Column 0x03: TreePath (8 bytes compact) → State trie node RLP (path length 6-15)
///   Column 0x05: TreePath (3 bytes: PathByte0, PathByte1, Length) → State trie node RLP (path length 0-5)
///   Column 0x06: TreePath.Path (32 bytes) + PathLength (1 byte) → State trie node RLP (path length 16+)
///   Column 0x07: AddressHash (20 bytes) → nested HSST (TreePath (8 bytes compact) → Storage trie node RLP, path length 6-15)
///   Column 0x08: AddressHash (20 bytes) → nested HSST (TreePath.Path (33 bytes) → Storage trie node RLP, path length 16+)
/// </summary>
public sealed class PersistedSnapshot : RefCountingDisposable
{
    // Tag prefixes for outer HSST columns
    internal static readonly byte[] MetadataTag = [0x00];
    internal static readonly byte[] AccountColumnTag = [0x01];
    internal static readonly byte[] StateNodeTag = [0x03];
    internal static readonly byte[] StateTopNodesTag = [0x05];
    internal static readonly byte[] StateNodeFallbackTag = [0x06];
    internal static readonly byte[] StorageNodeTag = [0x07];
    internal static readonly byte[] StorageNodeFallbackTag = [0x08];

    // Sub-tags within per-address HSST (sorted order)
    internal static readonly byte[] SlotSubTag = [0x01];
    internal static readonly byte[] SelfDestructSubTag = [0x02];
    internal static readonly byte[] AccountSubTag = [0x03];

    // Tiny per-snapshot CLOCK caches that skip the outer-column + entity-hash seeks on
    // repeat lookups. The cached Bound is the inner-HSST bound after seeking
    // (column-tag, address) for accounts and (StorageNodeTag, address-hash[..20]) for
    // storage trie. Bounds are stable for the lifetime of the snapshot since the data
    // is immutable; we only cache successful seeks (negative lookups go through the
    // bloom filter).
    private const int AddressBoundCacheCapacity = 8;
    private const int StorageBoundCacheCapacity = 8;

    private readonly ArenaReservation _reservation;
    private readonly Dictionary<int, PersistedSnapshot>? _referencedSnapshots;
    private readonly ClockCache<AddressAsKey, Bound> _addressBoundCache = new(AddressBoundCacheCapacity);
    private readonly ClockCache<Hash256AsKey, Bound> _storageBoundCache = new(StorageBoundCacheCapacity);

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
        Span<byte> nr = nrBuf[..localBound.Length];
        reader.TryRead(localBound.Offset, nr);
        NodeRef nodeRef = NodeRef.Read(nr);
        if (!_referencedSnapshots.TryGetValue(nodeRef.SnapshotId, out PersistedSnapshot? snap))
            throw new InvalidOperationException($"Referenced snapshot {nodeRef.SnapshotId} not found");
        return snap.ReadEntryValue(nodeRef.ValueLengthOffset);
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
    /// Resolve the per-address inner-HSST bound, hitting the address LRU first so repeat
    /// lookups for the same address skip the outer column-tag + 20-byte address seeks.
    /// Returns false (with default <paramref name="addressBound"/>) when the address is
    /// not present in this snapshot.
    /// </summary>
    private bool TryGetAddressBound(in ArenaByteReader reader, Address address, out Bound addressBound)
    {
        if (_addressBoundCache.TryGet(address, out addressBound))
            return true;
        if (!PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, address, out addressBound))
            return false;
        _addressBoundCache.Set(address, addressBound);
        return true;
    }

    private bool TryGetStorageBound(in ArenaByteReader reader, Hash256 address, out Bound storageBound)
    {
        if (_storageBoundCache.TryGet(address, out storageBound))
            return true;
        if (!PersistedSnapshotReader.TryGetStorageHsstBound<ArenaByteReader, NoOpPin>(in reader, address, out storageBound))
            return false;
        _storageBoundCache.Set(address, storageBound);
        return true;
    }

    public bool TryGetAccount(PersistedSnapshotBloom bloom, Address address, out Account? account)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
        {
            account = null;
            return false;
        }
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetAccount<ArenaByteReader, NoOpPin>(in reader, addrBound, out Bound b))
        {
            account = null;
            return false;
        }
        // Presence-marker encoding: PersistedSnapshotReader.TryGetAccount filters out
        // length-0 (absent) entries; a present entry is either [0x00] = deleted or
        // RLP-bytes = present. Slim account RLP starts with a list header (0xc0+) so
        // the 0x00 marker never collides with a valid RLP first byte.
        Span<byte> buf = b.Length <= 256 ? stackalloc byte[256] : new byte[b.Length];
        Span<byte> rlp = buf[..b.Length];
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

    public bool TryGetSlot(PersistedSnapshotBloom bloom, Address address, in UInt256 index, ref SlotValue slotValue)
    {
        ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(address);
        if (!bloom.KeyBloom.MightContain(addrKey) || !bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.SlotKey(addrKey, in index)))
            return false;
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound) ||
            !PersistedSnapshotReader.TryGetSlot<ArenaByteReader, NoOpPin>(in reader, addrBound, in index, out Bound b))
            return false;
        Span<byte> buf = stackalloc byte[32];
        Span<byte> raw = buf[..b.Length];
        reader.TryRead(b.Offset, raw);
        slotValue = SlotValue.FromSpanWithoutLeadingZero(raw);
        return true;
    }

    public bool IsSelfDestructed(PersistedSnapshotBloom bloom, Address address)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
            return false;
        ArenaByteReader reader = CreateReader();
        return TryGetAddressBound(in reader, address, out Bound addrBound)
            && PersistedSnapshotReader.IsSelfDestructed<ArenaByteReader, NoOpPin>(in reader, addrBound);
    }

    /// <summary>
    /// Get the self-destruct flag with boolean distinction.
    /// Returns null if no self-destruct entry exists for this address.
    /// Returns true if this is a new account (value = 0x01), false if destructed (value = empty).
    /// </summary>
    public bool? TryGetSelfDestructFlag(PersistedSnapshotBloom bloom, Address address)
    {
        if (!bloom.KeyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
            return null;
        ArenaByteReader reader = CreateReader();
        if (!TryGetAddressBound(in reader, address, out Bound addrBound))
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

    public bool TryLoadStorageNodeRlp(PersistedSnapshotBloom bloom, Hash256 address, in TreePath path, out byte[]? nodeRlp)
    {
        if (!bloom.TrieBloom.MightContain(PersistedSnapshotBloomBuilder.StorageNodeKey(address, in path)))
        {
            nodeRlp = null;
            return false;
        }
        ArenaByteReader reader = CreateReader();
        Bound bound;
        if (TryGetStorageBound(in reader, address, out Bound storageBound))
        {
            if (!PersistedSnapshotReader.TryLoadStorageNodeRlpInBound<ArenaByteReader, NoOpPin>(in reader, storageBound, address, in path, out bound))
            {
                nodeRlp = null;
                return false;
            }
        }
        else if (!PersistedSnapshotReader.TryLoadStorageNodeRlp<ArenaByteReader, NoOpPin>(in reader, address, in path, out bound))
        {
            // Fallback path: even on a cache miss the address-hash may exist only in the
            // StorageNodeFallbackTag column (long path-length nodes), which the LRU does
            // not pre-position; defer to the original full-seek helper.
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
    /// Read the raw entry value at a given <c>MetadataStart</c> offset (the LEB128 ValueLength
    /// cursor). Decodes the LEB128 forward via the reader, then copies the preceding value
    /// bytes directly into a heap-allocated array.
    /// </summary>
    public byte[] ReadEntryValue(int valueLengthOffset)
    {
        ArenaByteReader reader = _reservation.CreateReader();
        int valueLength = 0;
        int shift = 0;
        int pos = valueLengthOffset;
        Span<byte> oneByte = stackalloc byte[1];
        while (true)
        {
            reader.TryRead(pos++, oneByte);
            byte b = oneByte[0];
            valueLength |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        byte[] result = new byte[valueLength];
        reader.TryRead(valueLengthOffset - valueLength, result);
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
