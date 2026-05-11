// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
///   Column 0x01: AddressHash (20 bytes) → per-address HSST {
///       0x01 (StorageTopSubTag):      nested HSST (TreePath (3 bytes) → NodeRef, path length 0-5)
///       0x02 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → NodeRef, path length 6-15)
///       0x03 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → NodeRef, path length 16+)
///       0x04 (SlotSubTag):            nested HSST (SlotPrefix(31) → nested ByteTagMap(SlotSuffix(1) → SlotValue))
///       0x05 (AccountSubTag):         raw account slim RLP bytes (empty = deleted account)
///       0x06 (SelfDestructSubTag):    raw SD flag bytes (empty = destructed, 0x01 = new account)
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

    // Sub-tags within per-address HSST (sorted byte order).
    internal static readonly byte[] StorageTopSubTag = [0x01];
    internal static readonly byte[] StorageCompactSubTag = [0x02];
    internal static readonly byte[] StorageFallbackSubTag = [0x03];
    internal static readonly byte[] SlotSubTag = [0x04];
    internal static readonly byte[] AccountSubTag = [0x05];
    internal static readonly byte[] SelfDestructSubTag = [0x06];

    private const int AddressBoundCacheSets = 8;

    private readonly ArenaReservation _reservation;
    // Single blob manager — every snapshot lives in one repo (small or large) and its
    // NodeRefs resolve exclusively through that repo's blob manager. Cross-tier
    // references are impossible by construction.
    private readonly IBlobArenaManager _blobs;
    private readonly int[] _referencedBlobArenaIds;
    private readonly SeqlockValueCache<ValueHash256, Bound> _addressBoundCache = new(AddressBoundCacheSets);

    public int Id { get; }
    public StateId From { get; }
    public StateId To { get; }

    /// <summary>
    /// Blob arena ids whose contents this snapshot references via <see cref="NodeRef"/>s
    /// stored in its metadata HSST. Each id is leased on construction and released on cleanup.
    /// </summary>
    public int[] ReferencedBlobArenaIds => _referencedBlobArenaIds;

    public long Size => _reservation.Size;

    internal ArenaReservation Reservation => _reservation;

    /// <summary>
    /// Begin a scoped whole-buffer read over this snapshot's reservation.
    /// </summary>
    public WholeReadSession BeginWholeReadSession() => _reservation.BeginWholeReadSession();

    /// <summary>
    /// Construct a reader over this snapshot's bytes.
    /// </summary>
    internal ArenaByteReader CreateReader() => _reservation.CreateReader();

    public PersistedSnapshot(int id, StateId from, StateId to, ArenaReservation reservation,
        IBlobArenaManager blobs, int[]? referencedBlobArenaIds = null)
    {
        Id = id;
        From = from;
        To = to;
        _reservation = reservation;
        _blobs = blobs;
        _referencedBlobArenaIds = referencedBlobArenaIds ?? [];

        _reservation.AcquireLease();
        // Acquire blob arena leases up-front. If any id is unknown to the manager,
        // release what we've already taken before bubbling out.
        int acquired = 0;
        try
        {
            foreach (int blobId in _referencedBlobArenaIds)
            {
                if (!_blobs.TryAcquireBlobArena(blobId))
                    throw new InvalidOperationException($"Blob arena {blobId} referenced by snapshot {id} not registered in this tier");
                acquired++;
            }
        }
        catch
        {
            for (int i = 0; i < acquired; i++)
                _blobs.ReleaseBlobArena(_referencedBlobArenaIds[i]);
            _reservation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Materialise the trie-node RLP at <paramref name="localBound"/>. The bound holds an
    /// 8-byte <see cref="NodeRef"/>; the actual RLP bytes live in a blob arena.
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
    /// Resolve the per-address inner-HSST bound, hitting the address-hash LRU first so
    /// repeat lookups for the same address-hash skip the outer column-tag + 20-byte
    /// address-hash seeks.
    /// </summary>
    private bool TryGetAddressBound(in ArenaByteReader reader, in ValueHash256 addressHash, out Bound addressBound)
    {
        if (_addressBoundCache.TryGetValue(in addressHash, out addressBound))
            return true;
        if (!PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, in addressHash, out addressBound))
            return false;
        _addressBoundCache.Set(in addressHash, addressBound);
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

    public bool IsSelfDestructed(in ValueHash256 addressHash)
    {
        ArenaByteReader reader = CreateReader();
        return TryGetAddressBound(in reader, in addressHash, out Bound addrBound)
            && PersistedSnapshotReader.IsSelfDestructed<ArenaByteReader, NoOpPin>(in reader, addrBound);
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
    /// Read the "ref_ids" list from a snapshot's metadata column — now interpreted as
    /// referenced <c>BlobArenaId</c>s rather than referenced snapshot ids.
    /// </summary>
    public static int[]? ReadRefIdsFromMetadata<TReader, TPin>(scoped in TReader reader)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct =>
        PersistedSnapshotReader.ReadRefIdsFromMetadata<TReader, TPin>(in reader);

    // Worst-case Merkle-Patricia branch node: 17 entries × (1-byte prefix + 32-byte hash)
    // plus a 3-byte long-list framing header ≈ 564 bytes. Round up to 568 so the read
    // covers any branch node in one pread.
    private const int MaxTrieNodeRlpBytes = 568;

    private byte[] ReadBlobArenaRlp(int blobArenaId, int offset)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(MaxTrieNodeRlpBytes);
        try
        {
            Span<byte> buf = rented.AsSpan(0, MaxTrieNodeRlpBytes);
            int bytesRead = _blobs.RandomRead(blobArenaId, offset, buf);
            Rlp.ValueDecoderContext ctx = new(buf[..bytesRead]);
            int totalLength = ctx.PeekNextRlpLength();
            byte[] result = new byte[totalLength];
            buf[..totalLength].CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    public bool TryAcquire() => TryAcquireLease();

    protected override void CleanUp()
    {
        _reservation.Dispose();
        foreach (int blobId in _referencedBlobArenaIds)
            _blobs.ReleaseBlobArena(blobId);
    }
}
