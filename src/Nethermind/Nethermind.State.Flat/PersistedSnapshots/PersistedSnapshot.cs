// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by columnar HSST data on disk (or in memory).
/// The outer HSST has 7 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, state root values
///   Column 0x01: Address (20 bytes) → per-address HSST {
///       0x01 (SlotSubTag):         nested HSST (SlotPrefix(30) → nested(SlotSuffix(2) → SlotValue))
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

    private readonly ArenaReservation _reservation;
    private readonly Dictionary<int, PersistedSnapshot>? _referencedSnapshots;
    private BloomFilter? _keyBloom;

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

    public int Size => _reservation.Size;

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
    internal SpanByteReader CreateReader() => _reservation.CreateReader();

    /// <summary>
    /// Materialise the value at <paramref name="localBound"/> in this snapshot's bytes,
    /// dereferencing across snapshots when this snapshot stores NodeRefs. Reads via the
    /// reader abstraction (no GetSpan), copying directly into a heap-allocated byte[].
    /// </summary>
    internal byte[] ResolveValueAt(Bound localBound)
    {
        SpanByteReader reader = _reservation.CreateReader();
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
        SpanByteReader bootReader = CreateReader();
        HasNodeRefs = PersistedSnapshotReader.CheckHasNodeRefsFlag<SpanByteReader, NoOpPin>(in bootReader);

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

    public bool TryGetAccount(Address address, out Account? account)
    {
        if (_keyBloom is not null && !_keyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
        {
            account = null;
            return false;
        }
        SpanByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryGetAccount<SpanByteReader, NoOpPin>(in reader, address, out Bound b))
        {
            account = null;
            return false;
        }
        if (b.Length == 0)
        {
            account = null;
            return true;
        }
        Span<byte> buf = b.Length <= 256 ? stackalloc byte[256] : new byte[b.Length];
        Span<byte> rlp = buf[..b.Length];
        reader.TryRead(b.Offset, rlp);
        Rlp.ValueDecoderContext ctx = new(rlp);
        account = AccountDecoder.Slim.Decode(ref ctx);
        return true;
    }

    public bool TryGetSlot(Address address, in UInt256 index, ref SlotValue slotValue)
    {
        if (_keyBloom is not null)
        {
            ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(address);
            if (!_keyBloom.MightContain(addrKey) || !_keyBloom.MightContain(PersistedSnapshotBloomBuilder.SlotKey(addrKey, in index)))
                return false;
        }
        SpanByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryGetSlot<SpanByteReader, NoOpPin>(in reader, address, in index, out Bound b))
            return false;
        Span<byte> buf = stackalloc byte[32];
        Span<byte> raw = buf[..b.Length];
        reader.TryRead(b.Offset, raw);
        slotValue = SlotValue.FromSpanWithoutLeadingZero(raw);
        return true;
    }

    public bool IsSelfDestructed(Address address)
    {
        if (_keyBloom is not null && !_keyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
            return false;
        SpanByteReader reader = CreateReader();
        return PersistedSnapshotReader.IsSelfDestructed<SpanByteReader, NoOpPin>(in reader, address);
    }

    /// <summary>
    /// Get the self-destruct flag with boolean distinction.
    /// Returns null if no self-destruct entry exists for this address.
    /// Returns true if this is a new account (value = 0x01), false if destructed (value = empty).
    /// </summary>
    public bool? TryGetSelfDestructFlag(Address address)
    {
        if (_keyBloom is not null && !_keyBloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(address)))
            return null;
        SpanByteReader reader = CreateReader();
        return PersistedSnapshotReader.TryGetSelfDestructFlag<SpanByteReader, NoOpPin>(in reader, address);
    }

    public bool TryLoadStateNodeRlp(scoped in TreePath path, out byte[]? nodeRlp)
    {
        SpanByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryLoadStateNodeRlp<SpanByteReader, NoOpPin>(in reader, in path, out Bound bound))
        {
            nodeRlp = null;
            return false;
        }
        nodeRlp = ResolveValueAt(bound);
        return true;
    }

    public bool TryLoadStorageNodeRlp(Hash256 address, in TreePath path, out byte[]? nodeRlp)
    {
        SpanByteReader reader = CreateReader();
        if (!PersistedSnapshotReader.TryLoadStorageNodeRlp<SpanByteReader, NoOpPin>(in reader, address, in path, out Bound bound))
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
    /// Read the raw entry value at a given <c>MetadataStart</c> offset (the LEB128 ValueLength
    /// cursor). Decodes the LEB128 forward via the reader, then copies the preceding value
    /// bytes directly into a heap-allocated array.
    /// </summary>
    public byte[] ReadEntryValue(int valueLengthOffset)
    {
        SpanByteReader reader = _reservation.CreateReader();
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

    internal long KeyBloomCount => _keyBloom?.Count ?? 0;

    internal void AttachKeyBloom(BloomFilter bloom) => _keyBloom = bloom;

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    public bool TryAcquire() => TryAcquireLease();

    protected override void CleanUp()
    {
        _keyBloom?.Dispose();
        _reservation.Dispose();
        if (_referencedSnapshots is not null)
        {
            foreach (PersistedSnapshot snapshot in _referencedSnapshots.Values)
                snapshot.Dispose();
        }
    }
}
