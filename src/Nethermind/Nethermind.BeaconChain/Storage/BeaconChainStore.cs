// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
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
}

/// <summary>Persistence for beacon blocks, states, execution payload envelopes, the canonical slot index, the root-to-children index, and driver metadata.</summary>
/// <remarks>
/// <para>
/// Blocks and states are stored as snappy-compressed SSZ. States are large (hundreds of MB), so
/// they are split into <see cref="StateChunkSize"/> uncompressed slices compressed independently,
/// keyed by <c>blockRoot ++ big-endian chunk index</c>, with a manifest (chunk count and
/// uncompressed length) under the bare block root.
/// </para>
/// <para>
/// The <see cref="BeaconChainDbColumns.BlockIndex"/> column carries two key shapes that cannot
/// collide: 8-byte big-endian slot keys for the canonical index, and <see cref="ChildrenKeyPrefix"/>
/// <c>++ blockRoot</c> (33 bytes) for the children index, whose value is
/// <c>state (1) ++ parent root (32) ++ child roots (32 each)</c>. A stored block's child list is
/// always complete: a database from before the index is rebuilt from its stored blocks when first
/// opened at schema version <see cref="ChildrenIndexSchemaVersion"/>, and every store and delete after
/// that goes through the index. A list kept for a block that is not stored is pending, and a first
/// store of that block completes it.
/// </para>
/// <para>
/// Blocks are read and written in the shape of the fork their slot belongs to, as
/// <see cref="SignedBeaconBlockCodec"/> decides. Without a spec no Gloas fork is known: every block
/// is stored and read as <see cref="SignedBeaconBlock"/> and a Gloas block is refused.
/// </para>
/// </remarks>
/// <param name="db">The beacon chain columns.</param>
/// <param name="spec">The network whose fork schedule decides each block's shape; <c>null</c> reads and writes the Fulu shape only.</param>
public class BeaconChainStore(IColumnsDb<BeaconChainDbColumns> db, BeaconChainSpec? spec = null)
{
    /// <summary>Layout version of every column; bump it whenever a change needs an existing database migrated or refused.</summary>
    public const uint CurrentSchemaVersion = ExecutionPayloadEnvelopesSchemaVersion;

    /// <summary>The first version whose children index is known to cover every stored block; an older database gets the index rebuilt.</summary>
    private const uint ChildrenIndexSchemaVersion = 2;

    /// <summary>The first version stamped by a build that knows <see cref="BeaconChainDbColumns.ExecutionPayloadEnvelopes"/>, so a build that does not refuses the database.</summary>
    private const uint ExecutionPayloadEnvelopesSchemaVersion = 3;

    /// <summary>Offset of <c>parent_root</c> in a serialized <c>SignedBeaconBlock</c>: the message offset and signature precede the message, whose slot and proposer index precede the root; the same in every fork.</summary>
    private const int ParentRootOffset = sizeof(uint) + BlsSignature.Length + sizeof(ulong) + sizeof(ulong);

    private const int StateChunkSize = 4 * 1024 * 1024;
    private const int StateManifestLength = sizeof(uint) + sizeof(ulong);

    /// <summary>Byte offset of <c>slot</c> in a stored state, the same in the Fulu and Gloas layouts: <c>genesis_time</c> (8) plus <c>genesis_validators_root</c> (32).</summary>
    private const int StateSlotOffset = 40;
    private const int AnchorValueLength = Hash256.Size + sizeof(ulong);
    private const byte ChildrenKeyPrefix = 0x01;
    private const int ChildrenKeyLength = 1 + Hash256.Size;
    private const int ChildrenHeaderLength = 1 + Hash256.Size;

    /// <summary>Children were linked before the block itself was stored (backfill, or a delete that left children behind); its store completes the entry.</summary>
    private const byte ChildrenPending = 0;
    /// <summary>The block is stored and the list is the full set of stored children.</summary>
    private const byte ChildrenComplete = 1;

    /// <summary>The most blocks one children index rebuild step reads and writes, which bounds its memory.</summary>
    internal const int ChildrenRebuildBatchSize = 1024;

    /// <summary><c>compute_min_epochs_for_block_requests()</c> (phase0/p2p-interface.md), the epochs ExecutionPayloadEnvelopesByRange/ByRoot must serve (gloas/p2p-interface.md).</summary>
    internal const ulong MinEpochsForBlockRequests = Presets.MinValidatorWithdrawabilityDelay + Presets.ChurnLimitQuotient / 2;

    /// <summary>The most slots one envelope prune batch covers, so a prune after a long gap never builds one unbounded batch.</summary>
    internal const ulong EnvelopePruneBatchSlots = 1024;

    /// <summary>Envelope column key of the lowest and highest slot (8 bytes big-endian each) that may still have envelopes; its 1-byte length never collides with slot (8) or root (32) keys.</summary>
    private static ReadOnlySpan<byte> EnvelopeBoundsKey => [0];
    private const int EnvelopeBoundsLength = 2 * sizeof(ulong);

    /// <summary><c>MAX_PAYLOAD_SIZE</c> (phase0/p2p-interface.md): no envelope a peer can send, and so none stored, is larger uncompressed.</summary>
    private const int MaxEnvelopeLength = 10 * 1024 * 1024;

    private readonly IDb _blocks = db.GetColumnDb(BeaconChainDbColumns.Blocks);
    private readonly IDb _blockIndex = db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
    private readonly IDb _states = db.GetColumnDb(BeaconChainDbColumns.States);
    private readonly IDb _metadata = db.GetColumnDb(BeaconChainDbColumns.Metadata);
    private readonly IDb _envelopes = db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
    private readonly Lock _envelopeIndexLock = new();

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

    /// <summary>Stores a block and links it into the children index of its parent.</summary>
    /// <remarks>
    /// Callers are the single import worker and the checkpoint-sync anchor write, never concurrent,
    /// so the read-modify-write of the children entries below needs no lock. The whole update is one
    /// write batch so a crash cannot leave a stored block missing from its parent's child list.
    /// </remarks>
    /// <exception cref="BeaconStateException">The block's shape is not the one of the fork its slot belongs to.</exception>
    /// <exception cref="InvalidOperationException">The block is a Gloas block and this store has no spec.</exception>
    public void PutForkedBlock(Hash256 root, ForkedSignedBeaconBlock block)
    {
        Hash256 parentRoot = block.ParentRoot;
        byte[] ssz = spec is not null
            ? SignedBeaconBlockCodec.Encode(block, spec)
            : block is ForkedSignedBeaconBlock.OfFulu fulu
                ? SignedBeaconBlock.Encode(fulu.Block)
                : throw new InvalidOperationException($"A store without a {nameof(BeaconChainSpec)} cannot store the {block.GetType().Name} block {root}: it would read back as the Fulu shape");

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        batch.GetColumnBatch(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(ssz));
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);

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

    /// <summary>Whether a block is stored under <paramref name="root"/>, without reading or decoding it.</summary>
    public bool HasBlock(Hash256 root) => _blocks.KeyExists(root.Bytes);

    /// <summary>Reads a pre-Gloas block; see <see cref="TryGetForkedBlock"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored block is a Gloas block.</exception>
    public bool TryGetBlock(Hash256 root, [NotNullWhen(true)] out SignedBeaconBlock? block)
    {
        if (!TryGetForkedBlock(root, out ForkedSignedBeaconBlock? forked))
        {
            block = null;
            return false;
        }

        block = forked is ForkedSignedBeaconBlock.OfFulu fulu
            ? fulu.Block
            : throw new InvalidOperationException($"{nameof(TryGetBlock)} cannot read the Gloas block {root} at slot {forked.Slot}; use {nameof(TryGetForkedBlock)}");
        return true;
    }

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

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        RemoveState(blockRoot, manifest, batch.GetColumnBatch(BeaconChainDbColumns.States));
    }

    private static void RemoveState(Hash256 blockRoot, byte[] manifest, IWriteBatch states)
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
    /// <remarks>A state whose slot cannot be read is not known to be at or below <paramref name="throughSlot"/>, so it is kept.</remarks>
    private void RemoveStatesThroughSlot(ulong throughSlot, Hash256 keepRoot, IWriteBatch states)
    {
        List<byte[]> manifestKeys = [];
        foreach (byte[] key in _states.GetAllKeys())
        {
            if (key.Length == Hash256.Size)
            {
                manifestKeys.Add(key);
            }
        }

        foreach (byte[] key in manifestKeys)
        {
            Hash256 root = new(key);
            if (root == keepRoot || _states.Get(key) is not { } manifest)
            {
                continue;
            }

            if (TryGetStateSlot(root, manifest, out ulong slot) && slot <= throughSlot)
            {
                RemoveState(root, manifest, states);
            }
        }
    }

    /// <summary>Stores a verified execution payload envelope under its beacon block root and indexes it by slot for <see cref="PruneExecutionPayloadEnvelopes"/>.</summary>
    /// <remarks>
    /// The slot indexed is the payload's <c>slot_number</c>, which <c>verify_execution_payload_envelope</c> (gloas/beacon-chain.md)
    /// holds equal to the block's slot, so the block is not read. The record, the slot index entry and the slot bounds are one write batch.
    /// </remarks>
    /// <exception cref="ArgumentException">The envelope has no payload, names a beacon block other than <paramref name="blockRoot"/>, or encodes to more than <c>MAX_PAYLOAD_SIZE</c> bytes.</exception>
    public void PutExecutionPayloadEnvelope(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope)
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

            bool bounded = TryGetEnvelopeBounds(out ulong lowest, out ulong highest);
            if (!bounded || slot < lowest || slot > highest)
            {
                envelopes.Set(EnvelopeBoundsKey, EnvelopeBounds(bounded ? Math.Min(lowest, slot) : slot, bounded ? Math.Max(highest, slot) : slot));
            }
        }
    }

    /// <summary>The slot of <paramref name="envelope"/>'s payload, once it is known to name <paramref name="blockRoot"/>.</summary>
    /// <exception cref="ArgumentException">The envelope has no payload, or names a beacon block other than <paramref name="blockRoot"/>.</exception>
    internal static ulong GetExecutionPayloadEnvelopeSlot(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope) =>
        envelope.Message is { Payload: { } payload, BeaconBlockRoot: { } namedRoot } && namedRoot == blockRoot
            ? payload.SlotNumber
            : throw new ArgumentException($"The envelope for {blockRoot} has no payload or names beacon block {envelope.Message?.BeaconBlockRoot?.ToString() ?? "none"}", nameof(envelope));

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
    /// <remarks>The bounds are read again for each batch, so <see cref="PutExecutionPayloadEnvelope"/> waits for one batch only and a slot it adds meanwhile is still pruned.</remarks>
    private bool PruneExecutionPayloadEnvelopeBatch(ulong keepFrom)
    {
        lock (_envelopeIndexLock)
        {
            if (!TryGetEnvelopeBounds(out ulong lowest, out ulong highest) || keepFrom <= lowest)
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
                envelopes.Set(EnvelopeBoundsKey, EnvelopeBounds(batchLast + 1, highest));
            }

            return true;
        }
    }

    /// <summary>The stored slot bounds, rebuilt from the slot index when the record is malformed or inverted; the caller holds <see cref="_envelopeIndexLock"/>.</summary>
    /// <returns><c>false</c> when no slot may have envelopes.</returns>
    private bool TryGetEnvelopeBounds(out ulong lowest, out ulong highest)
    {
        byte[]? value = _envelopes.Get(EnvelopeBoundsKey);
        if (value is null)
        {
            lowest = highest = 0;
            return false;
        }

        if (value.Length == EnvelopeBoundsLength)
        {
            lowest = BinaryPrimitives.ReadUInt64BigEndian(value);
            highest = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(sizeof(ulong)));
            if (lowest <= highest)
            {
                return true;
            }
        }

        return RebuildEnvelopeBounds(out lowest, out highest);
    }

    /// <summary>Replaces the slot bounds record with the lowest and highest slot the slot index holds, or removes it when the index is empty.</summary>
    /// <remarks>Scans every key of the column, so it runs only on a damaged record, which would otherwise leave slots outside the bounds that no prune visits.</remarks>
    private bool RebuildEnvelopeBounds(out ulong lowest, out ulong highest)
    {
        bool found = false;
        lowest = ulong.MaxValue;
        highest = 0;
        foreach (byte[] key in _envelopes.GetAllKeys())
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
            _envelopes.Set(EnvelopeBoundsKey, EnvelopeBounds(lowest, highest));
            return true;
        }

        _envelopes.Remove(EnvelopeBoundsKey);
        lowest = highest = 0;
        return false;
    }

    private static byte[] EnvelopeBounds(ulong lowest, ulong highest)
    {
        byte[] value = new byte[EnvelopeBoundsLength];
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

    public byte[]? GetMetadata(string key) => _metadata.Get(Encoding.UTF8.GetBytes(key));

    public void PutMetadata(string key, byte[] value) => _metadata.Set(Encoding.UTF8.GetBytes(key), value);

    /// <summary>Brings the database to <see cref="CurrentSchemaVersion"/>, or refuses one last written by a newer build.</summary>
    /// <remarks>
    /// A database with no version predates versioning and counts as version 0; version 1 added only
    /// the stamp itself, so that upgrade rewrites nothing. Version 2 rebuilds the children index from
    /// every stored block, so a database that held blocks before the index existed answers child
    /// queries as complete. Version 3 rewrites nothing, since the envelope column keeps its layout; the
    /// stamp makes a version-2 build, which never prunes that column, refuse the database.
    /// A newer version may hold key shapes this build does not know, so it is
    /// refused rather than reinterpreted, and left unstamped.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The database was written by a newer schema version, or holds a block record too short to be a signed beacon block.</exception>
    public void EnsureSchemaVersion()
    {
        uint version = TryGetSchemaVersion(out uint stored) ? stored : 0;
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"The beaconChain database has schema version {version}, newer than the {CurrentSchemaVersion} this build supports; delete the beaconChain database to checkpoint-sync again.");
        }

        if (version < ChildrenIndexSchemaVersion)
        {
            RebuildChildrenIndex();
        }

        if (version != CurrentSchemaVersion)
        {
            SetSchemaVersion(CurrentSchemaVersion);
        }
    }

    /// <summary>Replaces the whole children index with one derived from the stored blocks alone.</summary>
    /// <remarks>
    /// Every stored block ends up complete with its parent recorded, every parent that is not stored
    /// keeps a pending list of its stored children, and entries with no block behind them (tombstones,
    /// lists of deleted blocks) are dropped. Only the prefix of each record up to its parent root is
    /// decompressed, so blocks of every fork rebuild alike, and blocks are read and entries written
    /// <see cref="ChildrenRebuildBatchSize"/> at a time, so memory does not grow with the database. The routine is
    /// idempotent: it runs before the version stamp, and a crash in between only makes it run again.
    /// </remarks>
    private void RebuildChildrenIndex()
    {
        RemoveChildrenEntries();

        List<(Hash256 Root, Hash256 ParentRoot)> batch = new(ChildrenRebuildBatchSize);
        foreach (byte[] key in _blocks.GetAllKeys())
        {
            if (key.Length != Hash256.Size || !TryReadParentRoot(key, out Hash256? parentRoot))
            {
                continue;
            }

            batch.Add((new Hash256(key), parentRoot));
            if (batch.Count == ChildrenRebuildBatchSize)
            {
                WriteRebuiltChildren(batch);
                batch.Clear();
            }
        }

        WriteRebuiltChildren(batch);
    }

    private void RemoveChildrenEntries()
    {
        List<byte[]> stale = new(ChildrenRebuildBatchSize);
        foreach (byte[] key in _blockIndex.GetAllKeys())
        {
            if (key.Length != ChildrenKeyLength || key[0] != ChildrenKeyPrefix)
            {
                continue;
            }

            stale.Add(key);
            if (stale.Count == ChildrenRebuildBatchSize)
            {
                RemoveIndexKeys(stale);
                stale.Clear();
            }
        }

        RemoveIndexKeys(stale);
    }

    private void RemoveIndexKeys(List<byte[]> keys)
    {
        if (keys.Count == 0) return;

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        foreach (byte[] key in keys)
        {
            index.Remove(key);
        }
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

    /// <summary>Writes the own entry of each block in <paramref name="blocks"/> and links it under its parent, rewriting each touched entry once.</summary>
    private void WriteRebuiltChildren(List<(Hash256 Root, Hash256 ParentRoot)> blocks)
    {
        if (blocks.Count == 0) return;

        Dictionary<Hash256, byte[]> entries = [];
        Dictionary<Hash256, List<Hash256>> added = [];
        foreach ((Hash256 root, Hash256 parentRoot) in blocks)
        {
            byte[] ownEntry = LoadRebuiltEntry(entries, root) ?? NewChildrenEntry(ChildrenPending, parentRoot);
            ownEntry[0] = ChildrenComplete;
            parentRoot.Bytes.CopyTo(ownEntry.AsSpan(1));
            entries[root] = ownEntry;

            if (!added.TryGetValue(parentRoot, out List<Hash256>? siblings))
            {
                added[parentRoot] = siblings = [];
            }

            siblings.Add(root);
        }

        foreach ((Hash256 parentRoot, List<Hash256> children) in added)
        {
            byte[] parentEntry = LoadRebuiltEntry(entries, parentRoot) ?? NewChildrenEntry(ChildrenPending, Hash256.Zero);
            byte[] extended = new byte[parentEntry.Length + children.Count * Hash256.Size];
            parentEntry.CopyTo(extended, 0);
            for (int i = 0; i < children.Count; i++)
            {
                children[i].Bytes.CopyTo(extended.AsSpan(parentEntry.Length + i * Hash256.Size));
            }

            entries[parentRoot] = extended;
        }

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        Span<byte> entryKey = stackalloc byte[ChildrenKeyLength];
        foreach ((Hash256 root, byte[] entry) in entries)
        {
            ChildrenKey(root, entryKey);
            index.Set(entryKey, entry);
        }
    }

    /// <summary>The entry of <paramref name="root"/> as this batch or an earlier one left it.</summary>
    private byte[]? LoadRebuiltEntry(Dictionary<Hash256, byte[]> batchEntries, Hash256 root)
    {
        if (batchEntries.TryGetValue(root, out byte[]? entry))
        {
            return entry;
        }

        Span<byte> key = stackalloc byte[ChildrenKeyLength];
        ChildrenKey(root, key);
        return ReadChildrenEntry(key);
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
    /// The anchor is the finalized checkpoint (or the checkpoint-sync start), and only states above it can still be
    /// read by a justification or a replay, so older snapshots, old anchors and evicted checkpoint candidates are reclaimed
    /// here. The batch makes a crash leave either the previous anchor with its state or the new one, never an anchor without its state.
    /// Idempotent: a repeat finds nothing left to delete.
    /// </remarks>
    public void SetAnchor(Hash256 blockRoot, ulong slot)
    {
        byte[] value = new byte[AnchorValueLength];
        blockRoot.Bytes.CopyTo(value.AsSpan());
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(Hash256.Size), slot);

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        batch.GetColumnBatch(BeaconChainDbColumns.Metadata).Set(Encoding.UTF8.GetBytes(BeaconChainMetadataKeys.Anchor), value);
        RemoveStatesThroughSlot(slot, blockRoot, batch.GetColumnBatch(BeaconChainDbColumns.States));
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
