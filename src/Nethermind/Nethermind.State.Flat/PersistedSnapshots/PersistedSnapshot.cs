// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
///       0x02 (StorageCompactSubTag):  nested HSST (TreePath (8 bytes compact) → NodeRef, path length 8-15)
///       0x03 (StorageFallbackSubTag): nested HSST (TreePath.Path (33 bytes) → NodeRef, path length 16+)
///       0x04 (SlotSubTag):            nested HSST (SlotPrefix(30) → nested HSST(SlotSuffix(2) → SlotValue))
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
    /// Begin a scoped whole-buffer read over this snapshot's reservation.
    /// </summary>
    public WholeReadSession BeginWholeReadSession() => _reservation.BeginWholeReadSession();

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
    public PersistedSnapshot(StateId from, StateId to, ArenaReservation reservation,
        IBlobArenaManager blobManager)
    {
        From = from;
        To = to;
        _reservation = reservation;
        _blobManager = blobManager;
        _reservation.AcquireLease();
    }

    /// <summary>
    /// Read the snapshot's referenced blob arena ids from its on-disk metadata HSST. Allocates
    /// a fresh array per call — cache locally for hot loops. Returns null if the snapshot has
    /// no <c>ref_ids</c> entry (synthetic test snapshots whose metadata HSST was hand-rolled
    /// without the standard builder).
    /// </summary>
    public ushort[]? ReadReferencedBlobArenaIds()
    {
        using WholeReadSession session = _reservation.BeginWholeReadSession();
        WholeReadSessionReader reader = session.GetReader();
        return PersistedSnapshotReader.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
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

    private bool TryGetAddressBound(in ArenaByteReader reader, in ValueHash256 addressHash, out Bound addressBound) =>
        PersistedSnapshotReader.TryGetAddressHsstBound<ArenaByteReader, NoOpPin>(in reader, in addressHash, out addressBound);

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
    /// Read the "ref_ids" list from a snapshot's metadata column — now interpreted as
    /// referenced <c>BlobArenaId</c>s rather than referenced snapshot ids.
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
        ushort[]? refIds = ReadReferencedBlobArenaIds();
        if (refIds is null) return;
        foreach (ushort id in refIds)
            _blobManager.GetFile(id).PersistOnShutdown();
    }

    protected override void CleanUp()
    {
        // Read the leased id list before disposing the reservation — once the reservation's
        // last lease drops we can't open a whole-read session against its mmap.
        ushort[]? refIds = ReadReferencedBlobArenaIds();
        _reservation.Dispose();
        if (refIds is null) return;
        foreach (ushort id in refIds)
            // Drop this snapshot's lease on each blob file. GetFile is a lock-free array read
            // — the lease we acquired at construction kept the slot alive.
            _blobManager.GetFile(id).Dispose();
    }
}
