// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Merge.Plugin.SszRest;
using Snappier;

namespace Nethermind.BeaconChain.Storage;

/// <summary>Well-known keys of the <see cref="BeaconChainDbColumns.Metadata"/> column.</summary>
public static class BeaconChainMetadataKeys
{
    /// <summary>32-byte anchor block root followed by the 8-byte big-endian anchor slot.</summary>
    public const string Anchor = "anchor";
    public const string GenesisValidatorsRoot = "genesisValidatorsRoot";
    /// <summary>4-byte big-endian schema version the database was last opened with; absent in databases that predate versioning.</summary>
    public const string SchemaVersion = "schemaVersion";
    /// <summary>8-byte big-endian slot above which the canonical index holds no entry; written with every index update.</summary>
    public const string CanonicalIndexTopSlot = "canonicalIndexTopSlot";
    /// <summary>32-byte root followed by the 8-byte big-endian epoch of the last weak subjectivity checkpoint an anchor of this database proved.</summary>
    public const string WeakSubjectivityCheckpoint = "weakSubjectivityCheckpoint";
    /// <summary>32-byte block root, 8-byte big-endian block slot and 8-byte big-endian slot of the state the checkpoint sync anchored on, which proves any weak subjectivity checkpoint that state proved.</summary>
    public const string CheckpointSyncAnchor = "checkpointSyncAnchor";
    /// <summary>8-byte big-endian slot of the lowest anchor ever recorded, before any verified backfill.</summary>
    public const string EarliestBlockSlot = "earliestBlockSlot";
}

/// <summary>Persists beacon blocks, states, envelopes, canonical and children indexes, and metadata.</summary>
/// <remarks>
/// States use independently snappy-compressed StateChunkSize slices keyed by blockRoot ++ big-endian chunk index,
/// with chunk count and uncompressed length under the bare root. Blocks use snappy-compressed SSZ.
/// BlockIndex keys are disjoint: 8-byte slots, BlockSummaryKeyPrefix ++ root, and ChildrenKeyPrefix ++ root.
/// Children values contain state (1 byte), parent root (32), then child roots (32 each). Stored blocks have complete
/// child lists; absent parents have pending lists.
/// StateId indexes use prefix 4 for state-to-block roots and 3 for block-to-state roots, updated with blocks.
/// SignedBeaconBlockCodec selects the slot's fork shape; without a spec only Fulu blocks are supported.
/// </remarks>
/// <param name="db">The beacon chain columns.</param>
/// <param name="spec">The fork schedule; null supports only Fulu shapes.</param>
public partial class BeaconChainStore(IColumnsDb<BeaconChainDbColumns> db, BeaconChainSpec? spec = null)
{
    /// <summary>Layout version of every column; bump it whenever a change needs an existing database migrated or refused.</summary>
    public const uint CurrentSchemaVersion = 6;

    /// <summary>Offset of <c>parent_root</c> in a serialized <c>SignedBeaconBlock</c>: the message offset and signature precede the message, whose slot and proposer index precede the root; the same in every fork.</summary>
    private const int ParentRootOffset = sizeof(uint) + BlsSignature.Length + sizeof(ulong) + sizeof(ulong);

    private const int StateChunkSize = 4 * 1024 * 1024;
    private const int StateManifestLength = sizeof(uint) + sizeof(ulong);

    /// <summary>Byte offset of <c>slot</c> in a stored state, the same in the Fulu and Gloas layouts: <c>genesis_time</c> (8) plus <c>genesis_validators_root</c> (32).</summary>
    private const int StateSlotOffset = 40;

    /// <summary>A state slot index entry is the 8-byte big-endian slot followed by the block root, with an empty value, in a separate column sorted by slot.</summary>
    private const int StateSlotIndexKeyLength = sizeof(ulong) + Hash256.Size;
    private const int AnchorValueLength = Hash256.Size + sizeof(ulong);
    private const byte ChildrenKeyPrefix = 0x01;
    private const int ChildrenKeyLength = 1 + Hash256.Size;
    private const int ChildrenHeaderLength = 1 + Hash256.Size;

    internal const byte BlockSummaryKeyPrefix = 0x02;
    private const int BlockSummaryKeyLength = 1 + Hash256.Size;
    private const int BlockSummaryHeaderLength = 1 + sizeof(ulong);
    private const byte FuluBlockSummary = 0;
    private const byte GloasBlockSummary = 1;

    /// <summary>Children were linked before the block itself was stored (backfill, or a delete that left children behind); its store completes the entry.</summary>
    private const byte ChildrenPending = 0;
    /// <summary>The block is stored and the list is the full set of stored children.</summary>
    private const byte ChildrenComplete = 1;

    /// <summary><c>compute_min_epochs_for_block_requests()</c> (phase0/p2p-interface.md), the epochs ExecutionPayloadEnvelopesByRange/ByRoot must serve (gloas/p2p-interface.md).</summary>
    internal const ulong MinEpochsForBlockRequests = Presets.MinValidatorWithdrawabilityDelay + Presets.ChurnLimitQuotient / 2;

    internal const ulong EnvelopePruneBatchSlots = 1024;

    /// <summary>Envelope column key of the lowest and highest slot (8 bytes big-endian each) that may still have envelopes; its 1-byte length never collides with slot (8) or root (32) keys.</summary>
    private static ReadOnlySpan<byte> EnvelopeBoundsKey => [0];
    private const int SlotBoundsLength = 2 * sizeof(ulong);

    /// <summary><c>MAX_PAYLOAD_SIZE</c> (phase0/p2p-interface.md): no envelope a peer can send, and so none stored, is larger uncompressed.</summary>
    private const int MaxEnvelopeLength = 10 * 1024 * 1024;

    private readonly IDb _blocks = db.GetColumnDb(BeaconChainDbColumns.Blocks);
    private readonly IDb _blockIndex = db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
    // Legacy gossip backfills must not outlive block deletion (gloas/p2p-interface.md block-seen check).
    private readonly Lock _blockSummaryLock = new();
    private readonly IDb _states = db.GetColumnDb(BeaconChainDbColumns.States);
    private readonly IDb _stateSlotIndex = db.GetColumnDb(BeaconChainDbColumns.StateSlotIndex);
    private readonly IDb _metadata = db.GetColumnDb(BeaconChainDbColumns.Metadata);
    private readonly IDb _envelopes = db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
    private readonly Lock _envelopeIndexLock = new();
    private readonly IDb _dataColumns = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
    private readonly Lock _columnIndexLock = new();

    internal const ulong ColumnPruneBatchSlots = 1024;

    /// <summary>Bounds prune work per call; later calls resume the backlog.</summary>
    internal const int MaxColumnPruneBatchesPerCall = 8;

    /// <summary>Column table keys never collide by length: 40 bytes is <c>root ++ index</c>, 8 is a slot index entry, 1 is one of the records below.</summary>
    private const int ColumnKeyLength = Hash256.Size + sizeof(ulong);
    private static ReadOnlySpan<byte> ColumnBoundsKey => [0];
    private static ReadOnlySpan<byte> ColumnFloorKey => [1];
    private static ReadOnlySpan<byte> ColumnCheckedKey => [2];

    /// <summary>A slot index entry is a block root followed by the 128-bit big-endian bitmap of its stored columns.</summary>
    private const int ColumnSlotEntryLength = Hash256.Size + 16;

    /// <summary>A record is a shape byte, the 8-byte big-endian slot and the SSZ sidecar.</summary>
    private const int ColumnRecordHeaderLength = 1 + sizeof(ulong);
    private const byte FuluColumnRecord = 0;
    private const byte GloasColumnRecord = 1;

    /// <summary>Stores a pre-Gloas block; see <see cref="PutForkedBlock"/>.</summary>
    /// <exception cref="InvalidOperationException">The block's slot is in the Gloas fork.</exception>
    public void PutBlock(Hash256 root, SignedBeaconBlock block)
    {
        if (spec is not null && SignedBeaconBlockCodec.IsGloasSlot(block.Message!.Slot, spec))
        {
            throw new InvalidOperationException($"{nameof(PutBlock)} cannot store the Gloas block {root} at slot {block.Message.Slot}; use {nameof(PutForkedBlock)}");
        }

        PutForkedBlock(root, new ForkedSignedBeaconBlock.OfFulu(block));
    }

    /// <summary>Stores a block and updates its parent's children index atomically.</summary>
    /// <remarks>The single import worker and checkpoint-sync writes do not overlap, so children read-modify-write needs no lock.</remarks>
    /// <exception cref="BeaconStateException">The block shape disagrees with its slot's fork.</exception>
    /// <exception cref="InvalidOperationException">The block is Gloas and this store has no spec.</exception>
    public void PutForkedBlock(Hash256 root, ForkedSignedBeaconBlock block)
    {
        using Lock.Scope summaryScope = _blockSummaryLock.EnterScope();
        Hash256 parentRoot = block.ParentRoot;
        byte[] ssz = spec is not null
            ? SignedBeaconBlockCodec.Encode(block, spec)
            : block is ForkedSignedBeaconBlock.OfFulu fulu
                ? SignedBeaconBlock.Encode(fulu.Block)
                : throw new InvalidOperationException($"A store without a {nameof(BeaconChainSpec)} cannot store the {block.GetType().Name} block {root}: it would read back as the Fulu shape");

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        batch.GetColumnBatch(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(ssz));
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        PutStateRootIndex(index, root, ssz);

        Span<byte> summaryKey = stackalloc byte[BlockSummaryKeyLength];
        BlockSummaryKey(root, summaryKey);
        index.Set(summaryKey, EncodeBlockSummary(block));

        Span<byte> parentKey = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(parentRoot, parentKey);
        // A parent with no entry has not been stored through the index (yet): its list is tracked
        // from here on, and its own store decides whether it can ever be called complete.
        byte[] parentEntry = ReadChildrenEntry(parentKey) ?? NewChildrenEntry(ChildrenPending, Hash256.Zero);
        if (!ContainsChild(parentEntry, root))
        {
            byte[] extended = new byte[parentEntry.Length + Hash256.Size];
            parentEntry.CopyTo(extended, 0);
            root.Bytes.CopyTo(extended.AsSpan(parentEntry.Length));
            parentEntry = extended;
        }

        index.Set(parentKey, parentEntry);

        Span<byte> ownKey = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(root, ownKey);
        byte[] ownEntry = ReadChildrenEntry(ownKey) ?? NewChildrenEntry(ChildrenPending, parentRoot);
        ownEntry[0] = ChildrenComplete;
        parentRoot.Bytes.CopyTo(ownEntry.AsSpan(1));
        index.Set(ownKey, ownEntry);
    }

    /// <summary>
    /// The roots of every stored block whose parent is <paramref name="root"/>.
    /// </summary>
    /// <param name="complete">
    /// Whether <paramref name="children"/> is the full set of children this node currently stores:
    /// true for every stored block, false while <paramref name="root"/> itself is not stored.
    /// </param>
    /// <returns><c>true</c> when the index has an entry for <paramref name="root"/> at all.</returns>
    public bool TryGetChildren(Hash256 root, out Hash256[] children, out bool complete)
    {
        Span<byte> key = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(root, key);
        byte[]? entry = ReadChildrenEntry(key);
        if (entry is null)
        {
            children = [];
            complete = false;
            return false;
        }

        complete = entry[0] == ChildrenComplete;
        children = new Hash256[(entry.Length - ChildrenHeaderLength) / Hash256.Size];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = new Hash256(entry.AsSpan(ChildrenHeaderLength + i * Hash256.Size, Hash256.Size));
        }

        return true;
    }

    private static void ChildrenKey(Hash256 root, Span<byte> key)
    {
        key[0] = ChildrenKeyPrefix;
        root.Bytes.CopyTo(key[1..]);
    }

    /// <summary>A well-formed children entry, or <c>null</c> when absent or unreadable; always a private copy, since the in-memory db hands out its stored array.</summary>
    private byte[]? ReadChildrenEntry(ReadOnlySpan<byte> key)
    {
        byte[]? entry = _blockIndex.Get(key);
        return entry is null || entry.Length < ChildrenHeaderLength || (entry.Length - ChildrenHeaderLength) % Hash256.Size != 0
            ? null
            : entry.AsSpan().ToArray();
    }

    private static byte[] NewChildrenEntry(byte state, Hash256 parentRoot)
    {
        byte[] entry = new byte[ChildrenHeaderLength];
        entry[0] = state;
        parentRoot.Bytes.CopyTo(entry.AsSpan(1));
        return entry;
    }

    private static bool ContainsChild(byte[] entry, Hash256 child)
    {
        for (int offset = ChildrenHeaderLength; offset + Hash256.Size <= entry.Length; offset += Hash256.Size)
        {
            if (entry.AsSpan(offset, Hash256.Size).SequenceEqual(child.Bytes)) return true;
        }

        return false;
    }

    /// <summary>Reads the stored slot and bid for gloas/p2p-interface.md sidecar validation without decoding the block.</summary>
    /// <returns><c>false</c> when the block was stored before the summary index existed, or the entry is malformed; <see cref="PutBlockSummary"/> then backfills it.</returns>
    /// <remarks>Written in the same batch as the block, so a stored block has one unless it predates the index.</remarks>
    internal bool TryGetBlockSummary(Hash256 root, out StoredBlockSummary summary)
    {
        Span<byte> key = stackalloc byte[BlockSummaryKeyLength];
        BlockSummaryKey(root, key);
        return TryDecodeBlockSummary(_blockIndex.Get(key), out summary);
    }

    private static bool TryDecodeBlockSummary(byte[]? value, out StoredBlockSummary summary)
    {
        if (value is null
            || value.Length < BlockSummaryHeaderLength
            || value.Length > BlockSummaryHeaderLength + Eip7594DasConstants.MaxBlobCommitmentsPerBlock * SszKzgCommitment.KzgCommitmentLength
            || value[0] > GloasBlockSummary
            || (value[0] == FuluBlockSummary && value.Length != BlockSummaryHeaderLength)
            || (value.Length - BlockSummaryHeaderLength) % SszKzgCommitment.KzgCommitmentLength != 0)
        {
            summary = default;
            return false;
        }

        SszKzgCommitment[] commitments = new SszKzgCommitment[(value.Length - BlockSummaryHeaderLength) / SszKzgCommitment.KzgCommitmentLength];
        for (int i = 0; i < commitments.Length; i++)
        {
            commitments[i] = SszKzgCommitment.FromSpan(value.AsSpan(BlockSummaryHeaderLength + i * SszKzgCommitment.KzgCommitmentLength, SszKzgCommitment.KzgCommitmentLength));
        }

        summary = new StoredBlockSummary(BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(1)), value[0] == GloasBlockSummary, commitments);
        return true;
    }

    /// <summary>Persists the slot and bid used by gloas/p2p-interface.md sidecar validation for a legacy block.</summary>
    internal StoredBlockSummary PutBlockSummary(Hash256 root, ForkedSignedBeaconBlock block)
    {
        using Lock.Scope summaryScope = _blockSummaryLock.EnterScope();
        Span<byte> key = stackalloc byte[BlockSummaryKeyLength];
        BlockSummaryKey(root, key);
        byte[] value = EncodeBlockSummary(block);
        if (HasBlock(root))
        {
            _blockIndex.PutSpan(key, value);
        }

        TryDecodeBlockSummary(value, out StoredBlockSummary summary);
        return summary;
    }

    private static void BlockSummaryKey(Hash256 root, Span<byte> key)
    {
        key[0] = BlockSummaryKeyPrefix;
        root.Bytes.CopyTo(key[1..]);
    }

    private static byte[] EncodeBlockSummary(ForkedSignedBeaconBlock block)
    {
        SszKzgCommitment[] commitments = block is ForkedSignedBeaconBlock.OfGloas gloas
            ? gloas.Block.Message?.Body?.SignedExecutionPayloadBid?.Message?.BlobKzgCommitments ?? []
            : [];
        byte[] value = new byte[BlockSummaryHeaderLength + commitments.Length * SszKzgCommitment.KzgCommitmentLength];
        value[0] = block is ForkedSignedBeaconBlock.OfGloas ? GloasBlockSummary : FuluBlockSummary;
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(1), block.Slot);
        for (int i = 0; i < commitments.Length; i++)
        {
            commitments[i].AsSpan().CopyTo(value.AsSpan(BlockSummaryHeaderLength + i * SszKzgCommitment.KzgCommitmentLength));
        }

        return value;
    }

    /// <summary>Whether a block is stored under <paramref name="root"/>, without reading or decoding it.</summary>
    public bool HasBlock(Hash256 root) => _blocks.KeyExists(root.Bytes);

    /// <summary>Reads a block in the shape of the fork its slot belongs to.</summary>
    /// <exception cref="BeaconStateException">The stored record is not a well-formed signed beacon block prefix.</exception>
    public bool TryGetForkedBlock(Hash256 root, [NotNullWhen(true)] out ForkedSignedBeaconBlock? block)
    {
        byte[]? compressed = _blocks.Get(root.Bytes);
        if (compressed is null)
        {
            block = null;
            return false;
        }

        byte[] ssz = Snappy.DecompressToArray(compressed);
        if (spec is not null)
        {
            block = SignedBeaconBlockCodec.Decode(ssz, spec);
            return true;
        }

        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock fulu);
        block = new ForkedSignedBeaconBlock.OfFulu(fulu);
        return true;
    }

    /// <summary>Reads a stored block's slot from its SSZ prefix, decompressing only that prefix.</summary>
    /// <returns><c>false</c> when no block is stored under <paramref name="root"/>.</returns>
    /// <remarks>
    /// A <c>true</c> result also bounds the record's claimed uncompressed length by the spec
    /// <c>MAX_PAYLOAD_SIZE</c>, so a full decompression of it cannot be asked for an arbitrary allocation.
    /// </remarks>
    /// <exception cref="BeaconStateException">The record is not a well-formed signed beacon block prefix.</exception>
    /// <exception cref="System.IO.InvalidDataException">The record claims more than <c>MAX_PAYLOAD_SIZE</c> bytes, or its prefix is not valid raw snappy.</exception>
    internal bool TryGetBlockSlot(Hash256 root, out ulong slot)
    {
        byte[]? compressed = _blocks.Get(root.Bytes);
        if (compressed is null)
        {
            slot = 0;
            return false;
        }

        Span<byte> prefix = stackalloc byte[SignedBeaconBlockCodec.SlotPrefixLength];
        int written = DecompressSnappyPrefix(compressed, prefix, ReqRespFraming.MaxPayloadSize);
        if (!SignedBeaconBlockCodec.TryReadSlot(prefix[..written], out slot))
        {
            throw new BeaconStateException($"The record stored for block {root} does not start with a signed beacon block slot");
        }

        return true;
    }

    /// <summary>Decompresses the start of a raw snappy block into <paramref name="prefix"/>, reading no further than it needs.</summary>
    /// <returns>The bytes written: <paramref name="prefix"/>.Length, or the whole block when it is shorter.</returns>
    /// <exception cref="System.IO.InvalidDataException">The claimed length exceeds <paramref name="maxLength"/>, or the length varint or an element before the end of the prefix is malformed.</exception>
    private static int DecompressSnappyPrefix(ReadOnlySpan<byte> compressed, Span<byte> prefix, int maxLength)
    {
        int pos = 0;
        ulong length = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (pos == compressed.Length || shift > 28) throw new InvalidDataException("Snappy length varint is truncated or longer than 32 bits");
            byte b = compressed[pos++];
            length |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80) break;
        }

        if (length > (ulong)maxLength) throw new InvalidDataException($"Snappy block claims {length} bytes, above the {maxLength}-byte limit");

        int target = (int)Math.Min(length, (ulong)prefix.Length);
        int written = 0;
        while (written < target)
        {
            if (pos == compressed.Length) throw new InvalidDataException("Snappy block ends before its claimed length");
            byte tag = compressed[pos++];
            int tagType = tag & 3;
            int extra = tagType switch
            {
                0 => tag >> 2 < 60 ? 0 : (tag >> 2) - 59,
                1 => 1,
                2 => 2,
                _ => 4,
            };
            if (compressed.Length - pos < extra) throw new InvalidDataException("Snappy element is truncated");
            ulong operand = 0;
            for (int i = 0; i < extra; i++) operand |= (ulong)compressed[pos + i] << (8 * i);
            pos += extra;

            if (tagType == 0)
            {
                ulong literalLength = (extra == 0 ? (ulong)(tag >> 2) : operand) + 1;
                int take = (int)Math.Min(literalLength, (ulong)(target - written));
                if (compressed.Length - pos < take) throw new InvalidDataException("Snappy literal is truncated");
                compressed.Slice(pos, take).CopyTo(prefix[written..]);
                pos += take;
                written += take;
                continue;
            }

            // Copy elements per google/snappy format_description.txt section 2.2.
            int copyLength = tagType == 1 ? 4 + ((tag >> 2) & 7) : (tag >> 2) + 1;
            ulong offset = tagType == 1 ? ((ulong)(tag >> 5) << 8) | operand : operand;
            if (offset == 0 || offset > (ulong)written) throw new InvalidDataException("Snappy copy offset points outside the decompressed bytes");
            int end = Math.Min(written + copyLength, target);
            // Byte by byte: a copy may overlap the bytes it produces.
            for (; written < end; written++) prefix[written] = prefix[written - (int)offset];
        }

        return written;
    }

    /// <summary>Deletes a block and unlinks it from its parent's child list, so that list never names a block this node no longer holds.</summary>
    /// <remarks>
    /// The block's own entry survives while children are still stored under it, so a re-store finds
    /// them instead of starting a complete-looking empty list; it is dropped once the last one goes.
    /// The block itself is never decoded here.
    /// </remarks>
    public void DeleteBlock(Hash256 root)
    {
        using Lock.Scope summaryScope = _blockSummaryLock.EnterScope();
        if (!_blocks.KeyExists(root.Bytes))
        {
            return;
        }

        Span<byte> ownKey = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(root, ownKey);
        byte[]? ownEntry = ReadChildrenEntry(ownKey);

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        batch.GetColumnBatch(BeaconChainDbColumns.Blocks).Remove(root.Bytes);
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);

        Span<byte> summaryKey = stackalloc byte[BlockSummaryKeyLength];
        BlockSummaryKey(root, summaryKey);
        index.Remove(summaryKey);
        RemoveStateRootIndex(index, root);

        Hash256? parentRoot;
        if (ownEntry is null)
        {
            // An unreadable own entry must not leave the block named in its parent's list.
            if (!TryReadParentRootQuietly(root, out parentRoot))
            {
                return;
            }
        }
        else
        {
            if (ownEntry.Length == ChildrenHeaderLength)
            {
                index.Remove(ownKey);
            }
            else
            {
                // The list stays intact and merely waits for a re-store to vouch for it again.
                ownEntry[0] = ChildrenPending;
                index.Set(ownKey, ownEntry);
            }

            parentRoot = new Hash256(ownEntry.AsSpan(1, Hash256.Size));
        }

        Span<byte> parentKey = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(parentRoot, parentKey);
        byte[]? parentEntry = ReadChildrenEntry(parentKey);
        if (parentEntry is null || !ContainsChild(parentEntry, root))
        {
            return;
        }

        byte[] remaining = WithoutChild(parentEntry, root);
        if (remaining.Length == ChildrenHeaderLength && remaining[0] == ChildrenPending)
        {
            // A pending entry exists only for the sake of its children; with the last one gone it says nothing.
            index.Remove(parentKey);
        }
        else
        {
            index.Set(parentKey, remaining);
        }
    }

    private bool TryReadParentRootQuietly(Hash256 root, [NotNullWhen(true)] out Hash256? parentRoot)
    {
        try
        {
            return TryReadParentRoot(root.Bytes.ToArray(), out parentRoot);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            parentRoot = null;
            return false;
        }
    }

    private static byte[] WithoutChild(byte[] entry, Hash256 child)
    {
        byte[] result = new byte[entry.Length - Hash256.Size];
        entry.AsSpan(0, ChildrenHeaderLength).CopyTo(result);
        int written = ChildrenHeaderLength;
        for (int offset = ChildrenHeaderLength; offset + Hash256.Size <= entry.Length; offset += Hash256.Size)
        {
            ReadOnlySpan<byte> candidate = entry.AsSpan(offset, Hash256.Size);
            if (candidate.SequenceEqual(child.Bytes)) continue;
            candidate.CopyTo(result.AsSpan(written));
            written += Hash256.Size;
        }

        return written == result.Length ? result : result[..written];
    }

    private long _backfilledBlockFloor = long.MaxValue;

    /// <summary>The earliest block range verified by backfill in this process (fulu/p2p-interface.md).</summary>
    internal ulong? BackfilledBlockFloor
    {
        get => Volatile.Read(ref _backfilledBlockFloor) is long floor && floor != long.MaxValue ? (ulong)floor : null;
        set => Volatile.Write(ref _backfilledBlockFloor, value is { } floor ? (long)floor : long.MaxValue);
    }

    public void SetCanonicalRoot(ulong slot, Hash256 root)
    {
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, slot);
        _blockIndex.PutSpan(key, root.Bytes);
    }

    /// <summary>
    /// Applies one head change to the canonical index in a single write batch: each slot is set to its root, or cleared
    /// when the root is <c>null</c>, and <paramref name="topSlot"/> is recorded as the highest slot the index may hold.
    /// </summary>
    /// <remarks>Readers see the whole change or none of it, so an interrupted update cannot leave an index that mixes two chains.</remarks>
    public void ApplyCanonicalIndexChanges(IReadOnlyList<(ulong Slot, Hash256? Root)> changes, ulong topSlot)
    {
        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        foreach ((ulong slot, Hash256? root) in changes)
        {
            BinaryPrimitives.WriteUInt64BigEndian(key, slot);
            if (root is null)
            {
                index.Remove(key);
            }
            else
            {
                index.PutSpan(key, root.Bytes);
            }
        }

        byte[] top = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(top, topSlot);
        batch.GetColumnBatch(BeaconChainDbColumns.Metadata).Set(Encoding.UTF8.GetBytes(BeaconChainMetadataKeys.CanonicalIndexTopSlot), top);
    }

    /// <summary>The highest slot the canonical index may hold an entry for, as its last update recorded; <c>null</c> before the first.</summary>
    public ulong? GetCanonicalIndexTopSlot() =>
        GetMetadata(BeaconChainMetadataKeys.CanonicalIndexTopSlot) is { Length: sizeof(ulong) } value ? BinaryPrimitives.ReadUInt64BigEndian(value) : null;

    public bool TryGetCanonicalRoot(ulong slot, [NotNullWhen(true)] out Hash256? root)
    {
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, slot);
        byte[]? value = _blockIndex.Get(key);
        root = value is null ? null : new Hash256(value);
        return root is not null;
    }

    public void PutState(Hash256 blockRoot, ReadOnlySpan<byte> sszBytes)
    {
        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch states = batch.GetColumnBatch(BeaconChainDbColumns.States);
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.StateSlotIndex);
        if (_states.Get(blockRoot.Bytes) is { } previousManifest && TryGetStateSlot(blockRoot, previousManifest, out ulong previousSlot))
        {
            index.Remove(StateSlotIndexKey(previousSlot, blockRoot));
        }

        IWriteBatch roots = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        RemoveStateRootIndex(roots, blockRoot);
        if (_blocks.Get(blockRoot.Bytes) is { } compressedBlock)
        {
            Span<byte> prefix = stackalloc byte[StateRootOffset + Hash256.Size];
            if (DecompressSnappyPrefix(compressedBlock, prefix, ReqRespFraming.MaxPayloadSize) == prefix.Length)
            {
                PutStateRootIndex(roots, blockRoot, prefix);
            }
        }

        int chunkCount = (sszBytes.Length + StateChunkSize - 1) / StateChunkSize;
        Span<byte> chunkKey = stackalloc byte[Hash256.Size + sizeof(uint)];
        blockRoot.Bytes.CopyTo(chunkKey);
        for (int i = 0; i < chunkCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chunkKey[Hash256.Size..], (uint)i);
            int offset = i * StateChunkSize;
            states.Set(chunkKey, Snappy.CompressToArray(sszBytes.Slice(offset, Math.Min(StateChunkSize, sszBytes.Length - offset))));
        }

        byte[] manifest = new byte[StateManifestLength];
        BinaryPrimitives.WriteUInt32BigEndian(manifest, (uint)chunkCount);
        BinaryPrimitives.WriteUInt64BigEndian(manifest.AsSpan(sizeof(uint)), (ulong)sszBytes.Length);
        states.Set(blockRoot.Bytes, manifest);

        if (sszBytes.Length >= StateSlotOffset + sizeof(ulong))
        {
            index.Set(StateSlotIndexKey(BinaryPrimitives.ReadUInt64LittleEndian(sszBytes[StateSlotOffset..]), blockRoot), []);
        }
    }

    private static byte[] StateSlotIndexKey(ulong slot, Hash256 blockRoot)
    {
        byte[] key = new byte[StateSlotIndexKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(key, slot);
        blockRoot.Bytes.CopyTo(key.AsSpan(sizeof(ulong)));
        return key;
    }

    /// <returns><c>true</c> with the uncompressed SSZ bytes, or <c>false</c> when the state is missing or incomplete.</returns>
    public bool TryGetState(Hash256 blockRoot, [NotNullWhen(true)] out byte[]? sszBytes)
    {
        sszBytes = null;
        byte[]? manifest = _states.Get(blockRoot.Bytes);
        if (manifest is null || manifest.Length != StateManifestLength)
        {
            return false;
        }

        int chunkCount = (int)BinaryPrimitives.ReadUInt32BigEndian(manifest);
        int length = (int)BinaryPrimitives.ReadUInt64BigEndian(manifest.AsSpan(sizeof(uint)));
        byte[] buffer = new byte[length];

        Span<byte> chunkKey = stackalloc byte[Hash256.Size + sizeof(uint)];
        blockRoot.Bytes.CopyTo(chunkKey);
        for (int i = 0; i < chunkCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chunkKey[Hash256.Size..], (uint)i);
            byte[]? compressed = _states.Get(chunkKey);
            if (compressed is null)
            {
                return false;
            }

            int offset = i * StateChunkSize;
            int expected = Math.Min(StateChunkSize, length - offset);
            if (Snappy.GetUncompressedLength(compressed) != expected
                || Snappy.Decompress(compressed, buffer.AsSpan(offset, expected)) != expected)
            {
                return false;
            }
        }

        sszBytes = buffer;
        return true;
    }

    /// <summary>Deletes the state stored under <paramref name="blockRoot"/>, manifest and chunks in one write batch; a root with no state is left alone.</summary>
    public void DeleteState(Hash256 blockRoot)
    {
        if (_states.Get(blockRoot.Bytes) is not { } manifest)
        {
            return;
        }

        bool slotKnown = TryGetStateSlot(blockRoot, manifest, out ulong slot);
        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch states = batch.GetColumnBatch(BeaconChainDbColumns.States);
        RemoveState(blockRoot, manifest, states, batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex));
        if (slotKnown)
        {
            batch.GetColumnBatch(BeaconChainDbColumns.StateSlotIndex).Remove(StateSlotIndexKey(slot, blockRoot));
        }
    }

    private void RemoveState(Hash256 blockRoot, byte[] manifest, IWriteBatch states, IWriteBatch roots)
    {
        // A malformed manifest names no chunks, so only the manifest goes; chunk keys the manifest does not name are never read.
        uint chunkCount = manifest.Length == StateManifestLength ? BinaryPrimitives.ReadUInt32BigEndian(manifest) : 0;
        Span<byte> chunkKey = stackalloc byte[Hash256.Size + sizeof(uint)];
        blockRoot.Bytes.CopyTo(chunkKey);
        for (uint i = 0; i < chunkCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chunkKey[Hash256.Size..], i);
            states.Remove(chunkKey);
        }

        states.Remove(blockRoot.Bytes);
        RemoveStateRootIndex(roots, blockRoot);
    }

    /// <summary>The slot recorded in the state stored under <paramref name="blockRoot"/>, read from its first chunk without decoding the state.</summary>
    /// <returns><c>false</c> when the state is missing or its first chunk is too short or not valid snappy, so no slot is known.</returns>
    private bool TryGetStateSlot(Hash256 blockRoot, byte[] manifest, out ulong slot)
    {
        slot = 0;
        if (manifest.Length != StateManifestLength || BinaryPrimitives.ReadUInt32BigEndian(manifest) == 0)
        {
            return false;
        }

        Span<byte> chunkKey = stackalloc byte[Hash256.Size + sizeof(uint)];
        blockRoot.Bytes.CopyTo(chunkKey);
        if (_states.Get(chunkKey) is not { } chunk)
        {
            return false;
        }

        Span<byte> prefix = stackalloc byte[StateSlotOffset + sizeof(ulong)];
        try
        {
            if (DecompressSnappyPrefix(chunk, prefix, StateChunkSize) != prefix.Length)
            {
                return false;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        slot = BinaryPrimitives.ReadUInt64LittleEndian(prefix[StateSlotOffset..]);
        return true;
    }

    /// <summary>Queues the removal of every stored state at or below <paramref name="throughSlot"/> other than <paramref name="keepRoot"/>.</summary>
    /// <remarks>Only finalized index entries nominate states; a readable state slot must also be finalized before removal (fork-choice.md Store).</remarks>
    private void RemoveStatesThroughSlot(ulong throughSlot, Hash256 keepRoot, IWriteBatch states, IWriteBatch index, IWriteBatch roots)
    {
        foreach (byte[] indexKey in StateSlotIndexKeysThrough(throughSlot))
        {
            Hash256 root = new(indexKey.AsSpan(sizeof(ulong)));
            if (root == keepRoot)
            {
                continue;
            }

            if (_states.Get(root.Bytes) is { } manifest
                && TryGetStateSlot(root, manifest, out ulong slot)
                && slot <= throughSlot)
            {
                RemoveState(root, manifest, states, roots);
            }

            index.Remove(indexKey);
        }
    }

    /// <summary>The state slot index keys from slot 0 through <paramref name="throughSlot"/>, ascending.</summary>
    /// <remarks>A sorted store reads no key above the range; one that cannot seek is walked in key order and left at the first key past it.</remarks>
    private List<byte[]> StateSlotIndexKeysThrough(ulong throughSlot)
    {
        byte[] upper = new byte[StateSlotIndexKeyLength + 1];
        BinaryPrimitives.WriteUInt64BigEndian(upper, throughSlot);
        upper.AsSpan(sizeof(ulong), Hash256.Size).Fill(byte.MaxValue);

        List<byte[]> keys = [];
        if (_stateSlotIndex is ISortedKeyValueStore sorted)
        {
            using ISortedView view = sorted.GetViewBetween(new byte[sizeof(ulong)], upper);
            while (view.MoveNext())
            {
                if (view.CurrentKey.Length == StateSlotIndexKeyLength)
                {
                    keys.Add(view.CurrentKey.ToArray());
                }
            }

            return keys;
        }

        foreach (byte[] key in _stateSlotIndex.GetAllKeys(ordered: true))
        {
            if (key.AsSpan().SequenceCompareTo(upper) >= 0)
            {
                break;
            }

            if (key.Length == StateSlotIndexKeyLength)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>Stores a verified execution payload envelope under its beacon block root and indexes it by slot for <see cref="PruneExecutionPayloadEnvelopes"/>.</summary>
    /// <remarks>
    /// The slot indexed is the payload's <c>slot_number</c>, which <c>verify_execution_payload_envelope</c> (gloas/beacon-chain.md)
    /// holds equal to the block's slot, so the block is not read. The record, the slot index entry and the slot bounds are one write batch.
    /// </remarks>
    /// <exception cref="ArgumentException">The envelope has no payload, names a beacon block other than <paramref name="blockRoot"/>, or encodes to more than <c>MAX_PAYLOAD_SIZE</c> bytes.</exception>
    public void PutExecutionPayloadEnvelope(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope)
        => PutExecutionPayloadEnvelope(blockRoot, envelope, valid: false);

    /// <summary>Stores the envelope, index and any VALID marker in one batch so pruning cannot leave an orphan verdict.</summary>
    internal void PutExecutionPayloadEnvelope(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope, bool valid)
    {
        ulong slot = GetExecutionPayloadEnvelopeSlot(blockRoot, envelope);
        byte[] ssz = SignedExecutionPayloadEnvelope.Encode(envelope);
        if (ssz.Length > MaxEnvelopeLength)
        {
            throw new ArgumentException($"The envelope for {blockRoot} encodes to {ssz.Length} bytes, above MAX_PAYLOAD_SIZE, so no read would accept it", nameof(envelope));
        }

        byte[] record = Snappy.CompressToArray(ssz);
        byte[] slotKey = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);

        lock (_envelopeIndexLock)
        {
            using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            IWriteBatch envelopes = batch.GetColumnBatch(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
            envelopes.Set(blockRoot.Bytes, record);

            byte[] stored = _envelopes.Get(slotKey) ?? [];
            // A corrupt entry's partial root is dropped, or every root appended after it would be misaligned and never pruned.
            ReadOnlySpan<byte> roots = stored.AsSpan(0, stored.Length - stored.Length % Hash256.Size);
            if (!ContainsRoot(roots, blockRoot))
            {
                envelopes.Set(slotKey, [.. roots, .. blockRoot.Bytes]);
            }

            bool bounded = TryGetSlotBounds(_envelopes, EnvelopeBoundsKey, out ulong lowest, out ulong highest);
            if (!bounded || slot < lowest || slot > highest)
            {
                envelopes.Set(EnvelopeBoundsKey, SlotBounds(bounded ? Math.Min(lowest, slot) : slot, bounded ? Math.Max(highest, slot) : slot));
            }

            if (valid)
                envelopes.Set(ExecutionPayloadVerdictKey(blockRoot.Bytes), [1]);
        }
    }

    /// <exception cref="ArgumentException">The envelope has no payload or names a different block root.</exception>
    internal static ulong GetExecutionPayloadEnvelopeSlot(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope) =>
        envelope.Message is { Payload: { } payload, BeaconBlockRoot: { } namedRoot } && namedRoot == blockRoot
            ? payload.SlotNumber
            : throw new ArgumentException($"The envelope for {blockRoot} has no payload or names beacon block {envelope.Message?.BeaconBlockRoot?.ToString() ?? "none"}", nameof(envelope));

    internal void SetExecutionPayloadValid(Hash256 blockRoot)
    {
        lock (_envelopeIndexLock)
        {
            if (_envelopes.KeyExists(blockRoot.Bytes))
                _envelopes.Set(ExecutionPayloadVerdictKey(blockRoot.Bytes), [1]);
        }
    }

    internal bool IsExecutionPayloadValid(Hash256 blockRoot) =>
        _envelopes.Get(ExecutionPayloadVerdictKey(blockRoot.Bytes)) is [1];

    private static byte[] ExecutionPayloadVerdictKey(ReadOnlySpan<byte> root) => [1, .. root];

    /// <summary>Reads the execution payload envelope stored under <paramref name="blockRoot"/>.</summary>
    /// <exception cref="InvalidDataException">The stored record is not snappy-compressed SSZ of an envelope with a payload that names <paramref name="blockRoot"/>.</exception>
    public bool TryGetExecutionPayloadEnvelope(Hash256 blockRoot, [NotNullWhen(true)] out SignedExecutionPayloadEnvelope? envelope)
    {
        byte[]? record = _envelopes.Get(blockRoot.Bytes);
        if (record is null)
        {
            envelope = null;
            return false;
        }

        SignedExecutionPayloadEnvelope decoded;
        try
        {
            // A corrupt length header would otherwise allocate up to 2 GiB before the decompressor fails.
            int length = Snappy.GetUncompressedLength(record);
            if (length > MaxEnvelopeLength)
            {
                throw new InvalidDataException($"The envelope stored under {blockRoot} claims {length} bytes, above MAX_PAYLOAD_SIZE");
            }

            byte[] ssz = new byte[length];
            Snappy.Decompress(record, ssz);

            SignedExecutionPayloadEnvelope.Decode(ssz, out decoded);
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"The envelope stored under {blockRoot} is not snappy-compressed SSZ: {e.Message}", e);
        }

        if (decoded.Message is not { Payload: not null, BeaconBlockRoot: { } namedRoot } || namedRoot != blockRoot)
        {
            throw new InvalidDataException($"The envelope stored under {blockRoot} has no payload or names beacon block {decoded.Message?.BeaconBlockRoot?.ToString() ?? "none"}");
        }

        envelope = decoded;
        return true;
    }

    /// <summary>Deletes every stored execution payload envelope whose slot is below both the ExecutionPayloadEnvelopesByRange/ByRoot retention window as of <paramref name="currentEpoch"/> and <paramref name="finalizedSlot"/>.</summary>
    /// <remarks>
    /// The window is <c>[max(GLOAS_FORK_EPOCH, current_epoch - compute_min_epochs_for_block_requests()), current_epoch]</c>
    /// (gloas/p2p-interface.md), where <paramref name="currentEpoch"/> is the wall-clock epoch; no envelope precedes the Gloas fork,
    /// so the fork term never prunes more and is left out. Envelopes from the finalized block on are kept even outside the window,
    /// because replay from the finalized state verifies each child against its parent's payload. Only the stored slot bounds are
    /// visited, at most <see cref="EnvelopePruneBatchSlots"/> slots per write batch, and each batch moves the lower bound with its
    /// deletions, so an interrupted prune resumes where it stopped.
    /// </remarks>
    /// <param name="currentEpoch">The wall-clock epoch.</param>
    /// <param name="finalizedSlot">The slot of the finalized checkpoint's block, or of an earlier block that replay after a restart starts from.</param>
    /// <exception cref="InvalidOperationException">This store has no spec, so it knows no epoch length.</exception>
    public void PruneExecutionPayloadEnvelopes(ulong currentEpoch, ulong finalizedSlot)
    {
        BeaconChainSpec networkSpec = spec ?? throw new InvalidOperationException($"A store without a {nameof(BeaconChainSpec)} knows no epoch length to prune envelopes by");
        ulong windowEpoch = currentEpoch > MinEpochsForBlockRequests ? currentEpoch - MinEpochsForBlockRequests : 0;
        ulong windowStart = windowEpoch > ulong.MaxValue / networkSpec.SlotsPerEpoch ? ulong.MaxValue : windowEpoch * networkSpec.SlotsPerEpoch;
        ulong keepFrom = Math.Min(windowStart, finalizedSlot);

        while (PruneExecutionPayloadEnvelopeBatch(keepFrom))
        {
        }
    }

    /// <summary>Deletes the envelopes of at most <see cref="EnvelopePruneBatchSlots"/> slots from the lower slot bound up, stopping below <paramref name="keepFrom"/>.</summary>
    /// <returns><c>false</c> when no stored slot is below <paramref name="keepFrom"/>.</returns>
    /// <remarks>The bounds are read again for each batch, so <see cref="PutExecutionPayloadEnvelope(Hash256, SignedExecutionPayloadEnvelope)"/> waits for one batch only and a slot it adds meanwhile is still pruned.</remarks>
    private bool PruneExecutionPayloadEnvelopeBatch(ulong keepFrom)
    {
        lock (_envelopeIndexLock)
        {
            if (!TryGetSlotBounds(_envelopes, EnvelopeBoundsKey, out ulong lowest, out ulong highest) || keepFrom <= lowest)
            {
                return false;
            }

            bool prunesAll = keepFrom > highest;
            ulong last = prunesAll ? highest : keepFrom - 1;
            ulong batchLast = last - lowest >= EnvelopePruneBatchSlots ? lowest + EnvelopePruneBatchSlots - 1 : last;
            Span<byte> slotKey = stackalloc byte[sizeof(ulong)];
            using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            IWriteBatch envelopes = batch.GetColumnBatch(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
            for (ulong slot = lowest; ; slot++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
                if (_envelopes.Get(slotKey) is { } roots)
                {
                    for (int offset = 0; offset + Hash256.Size <= roots.Length; offset += Hash256.Size)
                    {
                        envelopes.Remove(roots.AsSpan(offset, Hash256.Size));
                        envelopes.Remove(ExecutionPayloadVerdictKey(roots.AsSpan(offset, Hash256.Size)));
                    }

                    envelopes.Remove(slotKey);
                }

                if (slot == batchLast)
                {
                    break;
                }
            }

            if (prunesAll && batchLast == last)
            {
                envelopes.Remove(EnvelopeBoundsKey);
            }
            else
            {
                envelopes.Set(EnvelopeBoundsKey, SlotBounds(batchLast + 1, highest));
            }

            return true;
        }
    }

    /// <summary>The stored slot bounds, rebuilt from the slot index when the record is malformed or inverted; the caller holds the table's index lock.</summary>
    /// <returns><c>false</c> when no slot may hold records.</returns>
    private static bool TryGetSlotBounds(IDb table, ReadOnlySpan<byte> boundsKey, out ulong lowest, out ulong highest)
    {
        byte[]? value = table.Get(boundsKey);
        if (value is null)
        {
            lowest = highest = 0;
            return false;
        }

        if (value.Length == SlotBoundsLength)
        {
            lowest = BinaryPrimitives.ReadUInt64BigEndian(value);
            highest = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(sizeof(ulong)));
            if (lowest <= highest)
            {
                return true;
            }
        }

        return RebuildSlotBounds(table, boundsKey, out lowest, out highest);
    }

    /// <summary>Replaces the slot bounds record with the lowest and highest slot the slot index holds, or removes it when the index is empty.</summary>
    /// <remarks>Scans every key of the column, so it runs only on a damaged record, which would otherwise leave slots outside the bounds that no prune visits.</remarks>
    private static bool RebuildSlotBounds(IDb table, ReadOnlySpan<byte> boundsKey, out ulong lowest, out ulong highest)
    {
        bool found = false;
        lowest = ulong.MaxValue;
        highest = 0;
        foreach (byte[] key in table.GetAllKeys())
        {
            if (key.Length != sizeof(ulong))
            {
                continue;
            }

            ulong slot = BinaryPrimitives.ReadUInt64BigEndian(key);
            lowest = Math.Min(lowest, slot);
            highest = Math.Max(highest, slot);
            found = true;
        }

        if (found)
        {
            table.Set(boundsKey, SlotBounds(lowest, highest));
            return true;
        }

        table.Remove(boundsKey);
        lowest = highest = 0;
        return false;
    }

    private static byte[] SlotBounds(ulong lowest, ulong highest)
    {
        byte[] value = new byte[SlotBoundsLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, lowest);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(sizeof(ulong)), highest);
        return value;
    }

    private static bool ContainsRoot(ReadOnlySpan<byte> roots, Hash256 root)
    {
        for (int offset = 0; offset + Hash256.Size <= roots.Length; offset += Hash256.Size)
        {
            if (roots.Slice(offset, Hash256.Size).SequenceEqual(root.Bytes)) return true;
        }

        return false;
    }

    /// <summary>Stores a verified Fulu data column sidecar under (<paramref name="blockRoot"/>, index) and indexes it by <paramref name="slot"/> for <see cref="PruneDataColumnSidecars"/>.</summary>
    /// <remarks>The record, its slot index entry and the slot bounds are one write batch. A record is stored raw: cells and proofs are high-entropy, so compression only costs CPU.</remarks>
    /// <exception cref="ArgumentException">The column index is out of range, or the sidecar encodes to more than <c>MAX_PAYLOAD_SIZE</c> bytes.</exception>
    public void PutDataColumnSidecar(Hash256 blockRoot, ulong slot, DataColumnSidecar sidecar) =>
        PutDataColumnRecord(blockRoot, slot, sidecar.Index, FuluColumnRecord, DataColumnSidecar.Encode(sidecar));

    /// <summary>Stores a verified Gloas data column sidecar under its own <c>beacon_block_root</c> and index; see <see cref="PutDataColumnSidecar(Hash256, ulong, DataColumnSidecar)"/>.</summary>
    /// <exception cref="ArgumentException">The sidecar names no beacon block root, its column index is out of range, or it encodes to more than <c>MAX_PAYLOAD_SIZE</c> bytes.</exception>
    public void PutDataColumnSidecar(DataColumnSidecarGloas sidecar) =>
        PutDataColumnRecord(sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar)),
            sidecar.Slot, sidecar.Index, GloasColumnRecord, DataColumnSidecarGloas.Encode(sidecar));

    private void PutDataColumnRecord(Hash256 blockRoot, ulong slot, ulong index, byte shape, byte[] ssz)
    {
        if (index >= Eip7594DasConstants.NumberOfColumns)
        {
            throw new ArgumentException($"Column index {index} is not below NUMBER_OF_COLUMNS", nameof(index));
        }

        if (ssz.Length > ReqRespFraming.MaxPayloadSize)
        {
            throw new ArgumentException($"The column sidecar for {blockRoot} encodes to {ssz.Length} bytes, above MAX_PAYLOAD_SIZE, so no read would accept it", nameof(ssz));
        }

        byte[] record = new byte[ColumnRecordHeaderLength + ssz.Length];
        record[0] = shape;
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(1), slot);
        ssz.CopyTo(record.AsSpan(ColumnRecordHeaderLength));
        Span<byte> key = stackalloc byte[ColumnKeyLength];
        WriteColumnKey(key, blockRoot, index);

        lock (_columnIndexLock)
        {
            using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            IWriteBatch columns = batch.GetColumnBatch(BeaconChainDbColumns.DataColumnSidecars);
            // A root re-added under another slot would leave its old slot's index entry behind, never pruned with the record.
            if (_dataColumns.Get(key) is { Length: >= ColumnRecordHeaderLength } stored
                && BinaryPrimitives.ReadUInt64BigEndian(stored.AsSpan(1)) is var storedSlot && storedSlot != slot)
            {
                UpdateColumnSlotIndex(columns, storedSlot, blockRoot, (int)index, present: false);
            }

            UpdateColumnSlotIndex(columns, slot, blockRoot, (int)index, present: true);
            columns.Set(key, record);

            // A sidecar verified before finalization can land below the non-canonical cursor, so that pass must visit its slot again.
            if (_dataColumns.Get(ColumnCheckedKey) is { Length: sizeof(ulong) } checkedFrom && BinaryPrimitives.ReadUInt64BigEndian(checkedFrom) > slot)
            {
                Span<byte> rewound = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64BigEndian(rewound, slot);
                columns.PutSpan(ColumnCheckedKey, rewound);
            }

            bool bounded = TryGetSlotBounds(_dataColumns, ColumnBoundsKey, out ulong lowest, out ulong highest);
            if (!bounded || slot < lowest || slot > highest)
            {
                columns.Set(ColumnBoundsKey, SlotBounds(bounded ? Math.Min(lowest, slot) : slot, bounded ? Math.Max(highest, slot) : slot));
            }
        }
    }

    /// <summary>Reads the Fulu data column sidecar stored under (<paramref name="blockRoot"/>, <paramref name="column"/>); <c>false</c> when none is stored or the record is a Gloas one.</summary>
    /// <exception cref="InvalidDataException">The stored record is not an SSZ sidecar of that column.</exception>
    public bool TryGetDataColumnSidecar(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecar? sidecar)
    {
        sidecar = null;
        if (!TryReadDataColumnRecord(blockRoot, column, FuluColumnRecord, out byte[]? record))
        {
            return false;
        }

        try
        {
            DataColumnSidecar.Decode(record.AsSpan(ColumnRecordHeaderLength), out sidecar);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"The column sidecar stored under {blockRoot} index {column} is not SSZ: {e.Message}", e);
        }

        return sidecar.Index == column
            ? true
            : throw new InvalidDataException($"The column sidecar stored under {blockRoot} index {column} holds index {sidecar.Index}");
    }

    /// <summary>Reads the Gloas data column sidecar stored under (<paramref name="blockRoot"/>, <paramref name="column"/>); <c>false</c> when none is stored or the record is a Fulu one.</summary>
    /// <exception cref="InvalidDataException">The stored record is not an SSZ sidecar of that root and column.</exception>
    public bool TryGetDataColumnSidecarGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar)
    {
        sidecar = null;
        if (!TryReadDataColumnRecord(blockRoot, column, GloasColumnRecord, out byte[]? record))
        {
            return false;
        }

        try
        {
            DataColumnSidecarGloas.Decode(record.AsSpan(ColumnRecordHeaderLength), out sidecar);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"The column sidecar stored under {blockRoot} index {column} is not SSZ: {e.Message}", e);
        }

        return sidecar.Index == column && sidecar.BeaconBlockRoot == blockRoot
            ? true
            : throw new InvalidDataException($"The column sidecar stored under {blockRoot} index {column} names another root or index");
    }

    private bool TryReadDataColumnRecord(Hash256 blockRoot, ulong column, byte shape, [NotNullWhen(true)] out byte[]? record)
    {
        record = null;
        if (column >= Eip7594DasConstants.NumberOfColumns)
        {
            return false;
        }

        Span<byte> key = stackalloc byte[ColumnKeyLength];
        WriteColumnKey(key, blockRoot, column);
        byte[]? stored = _dataColumns.Get(key);
        if (stored is null)
        {
            return false;
        }

        if (stored.Length < ColumnRecordHeaderLength || stored.Length - ColumnRecordHeaderLength > ReqRespFraming.MaxPayloadSize)
        {
            throw new InvalidDataException($"The column sidecar stored under {blockRoot} index {column} has an impossible length of {stored.Length}");
        }

        if (stored[0] != shape)
        {
            return false;
        }

        record = stored;
        return true;
    }

    /// <summary>Whether a record is stored under (<paramref name="blockRoot"/>, <paramref name="column"/>), without reading it.</summary>
    public bool HasDataColumnRecord(Hash256 blockRoot, ulong column)
    {
        if (column >= Eip7594DasConstants.NumberOfColumns)
        {
            return false;
        }

        Span<byte> key = stackalloc byte[ColumnKeyLength];
        WriteColumnKey(key, blockRoot, column);
        return _dataColumns.KeyExists(key);
    }

    /// <summary>The columns of <paramref name="blockRoot"/> the slot index holds at <paramref name="slot"/>, bit <c>i</c> set for column <c>i</c>.</summary>
    public UInt128 GetStoredDataColumns(ulong slot, Hash256 blockRoot)
    {
        Span<byte> slotKey = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
        if (_dataColumns.Get(slotKey) is not { } entries)
        {
            return UInt128.Zero;
        }

        for (int offset = 0; offset + ColumnSlotEntryLength <= entries.Length; offset += ColumnSlotEntryLength)
        {
            if (entries.AsSpan(offset, Hash256.Size).SequenceEqual(blockRoot.Bytes))
            {
                return BinaryPrimitives.ReadUInt128BigEndian(entries.AsSpan(offset + Hash256.Size));
            }
        }

        return UInt128.Zero;
    }

    /// <summary>Why a stored slot fails <see cref="FindIncompleteDataColumnSlot"/>.</summary>
    internal enum DataColumnShortfall
    {
        None,
        /// <summary>The canonical block's slot index entry lacks a required column.</summary>
        ColumnNotIndexed,
        /// <summary>The slot index names a column whose record is not stored.</summary>
        RecordMissing,
        /// <summary>The canonical block carries blobs, but the slot index holds none of its columns.</summary>
        NoColumns,
        /// <summary>The canonical block is not stored, so whether it needs columns is unknown.</summary>
        BlockMissing,
        /// <summary>The required columns are unknown.</summary>
        RequiredColumnsUnknown,
        /// <summary>Reading the store failed.</summary>
        ReadFailed,
    }

    /// <summary>The highest canonical slot from <paramref name="from"/> through <paramref name="through"/> whose stored data columns fall short of <paramref name="required"/>.</summary>
    /// <remarks>
    /// The scan runs down from <paramref name="through"/> and stops at the first shortfall, so every slot above the result is complete.
    /// Only a canonical block with blob commitments needs columns, and from Gloas only once its payload envelope is stored: a slot index
    /// entry shows it has some, and without one the block is read.
    /// A column counts when its record key exists; the record is not decoded. Slots before Fulu, or below the retention window as of
    /// <paramref name="currentEpoch"/> (fulu/p2p-interface.md), are not visited.
    /// </remarks>
    /// <param name="currentEpoch">The wall-clock epoch that sets the retention window; <c>null</c> does not bound the scan by it.</param>
    /// <param name="required">The columns such a block must have, bit <c>i</c> for column <c>i</c>; <c>null</c> fails the first block that needs columns.</param>
    /// <param name="cancellationToken">Checked before each slot, so a stopped node does not finish a scan of the whole window.</param>
    /// <returns>The slot, or <c>null</c> when every visited slot is complete.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal ulong? FindIncompleteDataColumnSlot(ulong from, ulong through, ulong? currentEpoch, UInt128? required, out DataColumnShortfall shortfall, CancellationToken cancellationToken = default)
    {
        if (spec is not null)
        {
            from = Math.Max(from, spec.FuluForkEpoch > ulong.MaxValue / spec.SlotsPerEpoch ? ulong.MaxValue : spec.FuluForkEpoch * spec.SlotsPerEpoch);
            if (currentEpoch is { } epoch)
            {
                from = Math.Max(from, DataAvailabilityBoundary.ComputeStartSlot(epoch, spec));
            }
        }

        shortfall = DataColumnShortfall.None;
        if (from > through)
        {
            return null;
        }

        Span<byte> key = stackalloc byte[ColumnKeyLength];
        for (ulong slot = through; ; slot--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shortfall = CheckStoredDataColumns(slot, required, key);
            cancellationToken.ThrowIfCancellationRequested();
            if (shortfall != DataColumnShortfall.None)
            {
                return slot;
            }

            if (slot == from)
            {
                return null;
            }
        }
    }

    private DataColumnShortfall CheckStoredDataColumns(ulong slot, UInt128? required, Span<byte> key)
    {
        try
        {
            if (!TryGetCanonicalRoot(slot, out Hash256? root))
            {
                return DataColumnShortfall.None;
            }

            // gloas/fork-choice.md checks availability when the payload envelope is imported; a builder can withhold it, and then no column was needed.
            if (spec is not null && SignedBeaconBlockCodec.IsGloasSlot(slot, spec) && !_envelopes.KeyExists(root.Bytes))
            {
                return DataColumnShortfall.None;
            }

            UInt128 stored = GetStoredDataColumns(slot, root);
            if (stored == UInt128.Zero)
            {
                return !TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block) ? DataColumnShortfall.BlockMissing
                    : BlobCommitmentCount(block) == 0 ? DataColumnShortfall.None
                    : DataColumnShortfall.NoColumns;
            }

            if (required is not { } mask)
            {
                return DataColumnShortfall.RequiredColumnsUnknown;
            }

            if ((stored & mask) != mask)
            {
                return DataColumnShortfall.ColumnNotIndexed;
            }

            for (UInt128 remaining = stored; remaining != UInt128.Zero; remaining &= remaining - UInt128.One)
            {
                WriteColumnKey(key, root, (ulong)UInt128.TrailingZeroCount(remaining));
                if (!_dataColumns.KeyExists(key))
                {
                    return DataColumnShortfall.RecordMissing;
                }
            }

            return DataColumnShortfall.None;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return DataColumnShortfall.ReadFailed;
        }
    }

    private static int BlobCommitmentCount(ForkedSignedBeaconBlock block) => block switch
    {
        ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Message?.Body?.BlobKzgCommitments?.Length ?? 0,
        ForkedSignedBeaconBlock.OfGloas gloas => gloas.Block.Message?.Body?.SignedExecutionPayloadBid?.Message?.BlobKzgCommitments?.Length ?? 0,
        _ => throw new NotSupportedException($"Unhandled block {block.GetType().Name}"),
    };

    /// <summary>The slot from which the store holds every data column sidecar it was given, as last recorded; <c>false</c> before the first.</summary>
    public bool TryGetDataColumnFloor(out ulong slot)
    {
        if (_dataColumns.Get(ColumnFloorKey) is { Length: sizeof(ulong) } value)
        {
            slot = BinaryPrimitives.ReadUInt64BigEndian(value);
            return true;
        }

        slot = 0;
        return false;
    }

    /// <summary>Records <paramref name="slot"/> as the data column floor unless it is lower than the recorded one, so the floor never decreases.</summary>
    public void RaiseDataColumnFloor(ulong slot)
    {
        lock (_columnIndexLock)
        {
            if (!TryGetDataColumnFloor(out ulong floor) || slot > floor)
            {
                Span<byte> value = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64BigEndian(value, slot);
                _dataColumns.PutSpan(ColumnFloorKey, value);
            }
        }
    }

    internal ulong GetDataColumnRetentionFloor(ulong currentEpoch) => DataAvailabilityBoundary.ComputeStartSlot(currentEpoch,
        spec ?? throw new InvalidOperationException($"A store without a {nameof(BeaconChainSpec)} knows no retention window to prune data column sidecars by"));

    /// <summary>Deletes every stored data column sidecar below the DataColumnSidecarsByRange/ByRoot retention window as of <paramref name="currentEpoch"/>, then the ones of blocks that are not canonical below <paramref name="finalizedSlot"/>.</summary>
    /// <remarks>
    /// The window is <see cref="DataAvailabilityBoundary.ComputeStartSlot"/> (fulu/p2p-interface.md). The floor is raised to the window start before
    /// anything is deleted, so an interrupted prune never claims sidecars it removed. Only the stored slot bounds are visited, at most
    /// <see cref="ColumnPruneBatchSlots"/> slots per write batch, and each batch moves its bound with its deletions, so an interrupted prune
    /// resumes where it stopped, and a call runs at most <see cref="MaxColumnPruneBatchesPerCall"/> batches of each kind. A slot with no canonical entry keeps its
    /// sidecars; above the canonical index top the block may still be indexed, so the non-canonical pass stops there and revisits the slot next call.
    /// </remarks>
    /// <param name="currentEpoch">The wall-clock epoch.</param>
    /// <param name="finalizedSlot">The slot below which the canonical index is final, so a block that differs from it can never become canonical.</param>
    /// <exception cref="InvalidOperationException">This store has no spec, so it knows no window.</exception>
    public void PruneDataColumnSidecars(ulong currentEpoch, ulong finalizedSlot)
    {
        ulong keepFrom = GetDataColumnRetentionFloor(currentEpoch);
        if (TryGetDataColumnFloor(out _))
        {
            RaiseDataColumnFloor(keepFrom);
        }

        for (int batches = 0; batches < MaxColumnPruneBatchesPerCall && PruneDataColumnBatch(keepFrom); batches++)
        {
        }

        for (int batches = 0; batches < MaxColumnPruneBatchesPerCall && DeleteNonCanonicalDataColumnBatch(finalizedSlot); batches++)
        {
        }
    }

    /// <returns><c>false</c> when no stored slot is below <paramref name="keepFrom"/>.</returns>
    private bool PruneDataColumnBatch(ulong keepFrom)
    {
        lock (_columnIndexLock)
        {
            if (!TryGetSlotBounds(_dataColumns, ColumnBoundsKey, out ulong lowest, out ulong highest) || keepFrom <= lowest)
            {
                return false;
            }

            bool prunesAll = keepFrom > highest;
            ulong last = prunesAll ? highest : keepFrom - 1;
            ulong batchLast = last - lowest >= ColumnPruneBatchSlots ? lowest + ColumnPruneBatchSlots - 1 : last;
            Span<byte> slotKey = stackalloc byte[sizeof(ulong)];
            Span<byte> key = stackalloc byte[ColumnKeyLength];
            using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            IWriteBatch columns = batch.GetColumnBatch(BeaconChainDbColumns.DataColumnSidecars);
            for (ulong slot = lowest; ; slot++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
                if (_dataColumns.Get(slotKey) is { } entries)
                {
                    for (int offset = 0; offset + ColumnSlotEntryLength <= entries.Length; offset += ColumnSlotEntryLength)
                    {
                        RemoveEntryRecords(columns, entries.AsSpan(offset, ColumnSlotEntryLength), key);
                    }

                    columns.Remove(slotKey);
                }

                if (slot == batchLast)
                {
                    break;
                }
            }

            if (prunesAll && batchLast == last)
            {
                columns.Remove(ColumnBoundsKey);
            }
            else
            {
                columns.Set(ColumnBoundsKey, SlotBounds(batchLast + 1, highest));
            }

            return true;
        }
    }

    /// <returns><c>false</c> when every stored slot below <paramref name="finalizedSlot"/> has been checked.</returns>
    private bool DeleteNonCanonicalDataColumnBatch(ulong finalizedSlot)
    {
        lock (_columnIndexLock)
        {
            if (!TryGetSlotBounds(_dataColumns, ColumnBoundsKey, out ulong lowest, out ulong highest))
            {
                return false;
            }

            ulong start = _dataColumns.Get(ColumnCheckedKey) is { Length: sizeof(ulong) } value ? Math.Max(lowest, BinaryPrimitives.ReadUInt64BigEndian(value)) : lowest;
            if (start >= finalizedSlot || start > highest)
            {
                return false;
            }

            ulong last = Math.Min(finalizedSlot - 1, highest);
            ulong batchLast = last - start >= ColumnPruneBatchSlots ? start + ColumnPruneBatchSlots - 1 : last;
            ulong? canonicalTop = GetCanonicalIndexTopSlot();
            ulong resume = batchLast + 1;
            bool stalled = false;
            Span<byte> slotKey = stackalloc byte[sizeof(ulong)];
            Span<byte> key = stackalloc byte[ColumnKeyLength];
            using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            IWriteBatch columns = batch.GetColumnBatch(BeaconChainDbColumns.DataColumnSidecars);
            for (ulong slot = start; ; slot++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
                if (_dataColumns.Get(slotKey) is { } entries)
                {
                    if (TryGetCanonicalRoot(slot, out Hash256? canonical))
                    {
                        DropNonCanonicalEntries(columns, slotKey, entries, canonical, key);
                    }
                    else if (!stalled && (canonicalTop is not { } top || slot > top))
                    {
                        // Above the canonical index top the block may still be indexed, so the next pass must look at this slot again.
                        stalled = true;
                        resume = slot;
                    }
                }

                if (slot == batchLast)
                {
                    break;
                }
            }

            Span<byte> next = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(next, resume);
            columns.PutSpan(ColumnCheckedKey, next);
            return !stalled && batchLast < last;
        }
    }

    private static void DropNonCanonicalEntries(IWriteBatch columns, ReadOnlySpan<byte> slotKey, byte[] entries, Hash256 canonical, Span<byte> key)
    {
        byte[] kept = new byte[entries.Length];
        int keptLength = 0;
        for (int offset = 0; offset + ColumnSlotEntryLength <= entries.Length; offset += ColumnSlotEntryLength)
        {
            ReadOnlySpan<byte> entry = entries.AsSpan(offset, ColumnSlotEntryLength);
            if (entry[..Hash256.Size].SequenceEqual(canonical.Bytes))
            {
                entry.CopyTo(kept.AsSpan(keptLength));
                keptLength += ColumnSlotEntryLength;
            }
            else
            {
                RemoveEntryRecords(columns, entry, key);
            }
        }

        if (keptLength == 0)
        {
            columns.Remove(slotKey);
        }
        else if (keptLength != entries.Length)
        {
            columns.PutSpan(slotKey, kept.AsSpan(0, keptLength));
        }
    }

    /// <summary>Removes the records of one slot index entry: a root and the bitmap of its stored columns.</summary>
    private static void RemoveEntryRecords(IWriteBatch columns, ReadOnlySpan<byte> entry, Span<byte> key)
    {
        UInt128 bits = BinaryPrimitives.ReadUInt128BigEndian(entry[Hash256.Size..]);
        entry[..Hash256.Size].CopyTo(key);
        for (int column = 0; column < Eip7594DasConstants.NumberOfColumns; column++)
        {
            if ((bits >> column & UInt128.One) != UInt128.Zero)
            {
                BinaryPrimitives.WriteUInt64BigEndian(key[Hash256.Size..], (ulong)column);
                columns.Remove(key);
            }
        }
    }

    /// <summary>Sets or clears one column in the slot index entry of a block root, adding or dropping the entry and the slot record as needed.</summary>
    private void UpdateColumnSlotIndex(IWriteBatch columns, ulong slot, Hash256 blockRoot, int column, bool present)
    {
        Span<byte> slotKey = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(slotKey, slot);
        byte[] stored = _dataColumns.Get(slotKey) ?? [];
        int count = stored.Length / ColumnSlotEntryLength;
        int at = -1;
        for (int i = 0; i < count; i++)
        {
            if (stored.AsSpan(i * ColumnSlotEntryLength, Hash256.Size).SequenceEqual(blockRoot.Bytes))
            {
                at = i;
                break;
            }
        }

        UInt128 bits = at < 0 ? UInt128.Zero : BinaryPrimitives.ReadUInt128BigEndian(stored.AsSpan(at * ColumnSlotEntryLength + Hash256.Size));
        UInt128 mask = UInt128.One << column;
        UInt128 next = present ? bits | mask : bits & ~mask;
        if (next == bits)
        {
            return;
        }

        int newCount = count - (at >= 0 ? 1 : 0) + (next == UInt128.Zero ? 0 : 1);
        if (newCount == 0)
        {
            columns.Remove(slotKey);
            return;
        }

        byte[] updated = new byte[newCount * ColumnSlotEntryLength];
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == at) continue;
            stored.AsSpan(i * ColumnSlotEntryLength, ColumnSlotEntryLength).CopyTo(updated.AsSpan(written));
            written += ColumnSlotEntryLength;
        }

        if (next != UInt128.Zero)
        {
            blockRoot.Bytes.CopyTo(updated.AsSpan(written));
            BinaryPrimitives.WriteUInt128BigEndian(updated.AsSpan(written + Hash256.Size), next);
        }

        columns.Set(slotKey, updated);
    }

    private static void WriteColumnKey(Span<byte> key, Hash256 blockRoot, ulong column)
    {
        blockRoot.Bytes.CopyTo(key);
        BinaryPrimitives.WriteUInt64BigEndian(key[Hash256.Size..], column);
    }

    public byte[]? GetMetadata(string key) => _metadata.Get(Encoding.UTF8.GetBytes(key));
    public void PutMetadata(string key, byte[] value) => _metadata.Set(Encoding.UTF8.GetBytes(key), value);

    /// <summary>Stamps an empty database or verifies that a populated database uses the current schema.</summary>
    /// <exception cref="InvalidOperationException">The database does not use the current schema.</exception>
    public void EnsureSchemaVersion()
    {
        if (TryGetSchemaVersion(out uint version))
        {
            if (version != CurrentSchemaVersion)
                throw new InvalidOperationException($"The beaconChain database has schema version {version}, but this build supports only {CurrentSchemaVersion}; delete the beaconChain database to checkpoint-sync again.");
            return;
        }

        foreach (BeaconChainDbColumns column in db.ColumnKeys)
        {
            using IEnumerator<byte[]> keys = db.GetColumnDb(column).GetAllKeys().GetEnumerator();
            if (keys.MoveNext())
                throw new InvalidOperationException("The beaconChain database has no valid schema version; delete the beaconChain database to checkpoint-sync again.");
        }

        SetSchemaVersion(CurrentSchemaVersion);
    }

    /// <returns><c>false</c> when the record is gone.</returns>
    /// <exception cref="InvalidOperationException">The record is too short to be a signed beacon block.</exception>
    private bool TryReadParentRoot(byte[] blockKey, [NotNullWhen(true)] out Hash256? parentRoot)
    {
        Span<byte> compressed = _blocks.GetSpan(blockKey);
        try
        {
            if (compressed.IsNull())
            {
                parentRoot = null;
                return false;
            }

            Span<byte> prefix = stackalloc byte[ParentRootOffset + Hash256.Size];
            int written = DecompressSnappyPrefix(compressed, prefix, ReqRespFraming.MaxPayloadSize);
            if (written < prefix.Length)
            {
                throw new InvalidOperationException($"The beaconChain database holds a {written}-byte block record under {new Hash256(blockKey)}, too short to be a signed beacon block; delete the beaconChain database to checkpoint-sync again.");
            }

            parentRoot = new Hash256(prefix[ParentRootOffset..]);
            return true;
        }
        finally
        {
            _blocks.DangerousReleaseMemory(compressed);
        }
    }

    public bool TryGetSchemaVersion(out uint version)
    {
        byte[]? value = GetMetadata(BeaconChainMetadataKeys.SchemaVersion);
        if (value is null || value.Length != sizeof(uint))
        {
            version = 0;
            return false;
        }

        version = BinaryPrimitives.ReadUInt32BigEndian(value);
        return true;
    }

    public void SetSchemaVersion(uint version)
    {
        byte[] value = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(value, version);
        PutMetadata(BeaconChainMetadataKeys.SchemaVersion, value);
    }

    /// <summary>Records <paramref name="blockRoot"/> as the anchor and deletes every stored state at or below <paramref name="slot"/> but its own, in one write batch.</summary>
    /// <remarks>
    /// Finalization or checkpoint sync sets the anchor; only newer states remain useful for justification
    /// or replay. The atomic batch preserves either anchor with its state across crashes and is idempotent.
    /// Canonical blocks below the anchor remain stored; <see cref="TryGetEarliestBlockSlot"/> retains the lowest anchor.
    /// </remarks>
    public void SetAnchor(Hash256 blockRoot, ulong slot)
    {
        byte[] value = new byte[AnchorValueLength];
        blockRoot.Bytes.CopyTo(value.AsSpan());
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(Hash256.Size), slot);
        byte[] earliest = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(earliest, TryGetEarliestBlockSlot(out ulong earliestSlot) ? Math.Min(earliestSlot, slot) : slot);

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch metadata = batch.GetColumnBatch(BeaconChainDbColumns.Metadata);
        metadata.Set(Encoding.UTF8.GetBytes(BeaconChainMetadataKeys.Anchor), value);
        metadata.Set(Encoding.UTF8.GetBytes(BeaconChainMetadataKeys.EarliestBlockSlot), earliest);
        RemoveStatesThroughSlot(slot, blockRoot, batch.GetColumnBatch(BeaconChainDbColumns.States), batch.GetColumnBatch(BeaconChainDbColumns.StateSlotIndex), batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex));
    }

    /// <summary>The slot of the lowest anchor ever recorded: canonical blocks are stored from it on.</summary>
    /// <remarks>A database written before this record falls back to its current anchor, which is never below the real floor.</remarks>
    internal bool TryGetEarliestBlockSlot(out ulong slot)
    {
        if (GetMetadata(BeaconChainMetadataKeys.EarliestBlockSlot) is { Length: sizeof(ulong) } value)
        {
            slot = BinaryPrimitives.ReadUInt64BigEndian(value);
            return true;
        }

        return TryGetAnchor(out _, out slot);
    }

    public bool TryGetAnchor([NotNullWhen(true)] out Hash256? blockRoot, out ulong slot)
    {
        byte[]? value = GetMetadata(BeaconChainMetadataKeys.Anchor);
        if (value is null || value.Length != AnchorValueLength)
        {
            blockRoot = null;
            slot = 0;
            return false;
        }

        blockRoot = new Hash256(value.AsSpan(0, Hash256.Size));
        slot = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(Hash256.Size));
        return true;
    }
}

/// <summary>The stored slot and bid commitments used by gloas/p2p-interface.md sidecar validation.</summary>
internal readonly record struct StoredBlockSummary(ulong Slot, bool IsGloas, SszKzgCommitment[] Commitments);
