// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
}

/// <summary>Persistence for beacon blocks, states, the canonical slot index, the root-to-children index, and driver metadata.</summary>
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
/// <c>state (1) ++ parent root (32) ++ child roots (32 each)</c>. A child list is only reported
/// complete for a block whose every store and delete went through this index; a block that existed
/// outside it may have children the index never saw, so it is pinned incomplete for good, even
/// across a delete and re-store. A database from before the index is rebuilt from its stored blocks
/// when first opened at schema version <see cref="ChildrenIndexSchemaVersion"/>, so no such block
/// survives an upgrade.
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
    public const uint CurrentSchemaVersion = ChildrenIndexSchemaVersion;

    /// <summary>The first version whose children index is known to cover every stored block; an older database gets the index rebuilt.</summary>
    private const uint ChildrenIndexSchemaVersion = 2;

    /// <summary>Offset of <c>parent_root</c> in a serialized <c>SignedBeaconBlock</c>: the message offset and signature precede the message, whose slot and proposer index precede the root; the same in every fork.</summary>
    private const int ParentRootOffset = sizeof(uint) + BlsSignature.Length + sizeof(ulong) + sizeof(ulong);

    private const int StateChunkSize = 4 * 1024 * 1024;
    private const int StateManifestLength = sizeof(uint) + sizeof(ulong);
    private const int AnchorValueLength = Hash256.Size + sizeof(ulong);
    private const byte ChildrenKeyPrefix = 0x01;
    private const int ChildrenKeyLength = 1 + Hash256.Size;
    private const int ChildrenHeaderLength = 1 + Hash256.Size;

    /// <summary>Children were linked before the block itself was stored through the index (backfill, or a delete that left children behind); a first store promotes it.</summary>
    private const byte ChildrenPending = 0;
    /// <summary>Every store and delete of the block went through the index: the list is the full set of stored children.</summary>
    private const byte ChildrenComplete = 1;
    /// <summary>The block existed outside the index at some point, so children may be missing for good.</summary>
    private const byte ChildrenNeverComplete = 2;

    private readonly IDb _blocks = db.GetColumnDb(BeaconChainDbColumns.Blocks);
    private readonly IDb _blockIndex = db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
    private readonly IDb _states = db.GetColumnDb(BeaconChainDbColumns.States);
    private readonly IDb _metadata = db.GetColumnDb(BeaconChainDbColumns.Metadata);

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
        bool firstStore = !_blocks.KeyExists(root.Bytes);

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
        if (ownEntry[0] == ChildrenPending)
        {
            // A block already in the store yet still pending was stored before the index existed:
            // only a first store can vouch that the list holds every child.
            ownEntry[0] = firstStore ? ChildrenComplete : ChildrenNeverComplete;
        }

        parentRoot.Bytes.CopyTo(ownEntry.AsSpan(1));
        index.Set(ownKey, ownEntry);
    }

    /// <summary>
    /// The roots of every stored block whose parent is <paramref name="root"/>.
    /// </summary>
    /// <param name="complete">
    /// Whether <paramref name="children"/> is the full set of children this node currently stores.
    /// False when <paramref name="root"/> ever existed outside the index (it was stored before the
    /// index shipped), in which case children may be missing and the list must not be presented as
    /// an answer.
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

    /// <summary>Deletes a block and unlinks it from its parent's child list, so that list never names a block this node no longer holds.</summary>
    /// <remarks>
    /// The block's own entry survives while children are still stored under it, so a re-store finds
    /// them instead of starting a complete-looking empty list; it is dropped once the last one goes.
    /// A stored block with no entry predates the index and leaves a tombstone, because its children
    /// may predate it too. The block itself is never decoded here.
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

        if (ownEntry is null)
        {
            index.Set(ownKey, NewChildrenEntry(ChildrenNeverComplete, Hash256.Zero));
            return;
        }

        if (ownEntry.Length == ChildrenHeaderLength && ownEntry[0] != ChildrenNeverComplete)
        {
            index.Remove(ownKey);
        }
        else
        {
            // A complete list stays intact and merely waits for a re-store to vouch for it again; a
            // stored block that was still pending never went through the index at all.
            ownEntry[0] = ownEntry[0] == ChildrenComplete ? ChildrenPending : ChildrenNeverComplete;
            index.Set(ownKey, ownEntry);
        }

        Hash256 parentRoot = new(ownEntry.AsSpan(1, Hash256.Size));
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

    public byte[]? GetMetadata(string key) => _metadata.Get(Encoding.UTF8.GetBytes(key));

    public void PutMetadata(string key, byte[] value) => _metadata.Set(Encoding.UTF8.GetBytes(key), value);

    /// <summary>Brings the database to <see cref="CurrentSchemaVersion"/>, or refuses one last written by a newer build.</summary>
    /// <remarks>
    /// A database with no version predates versioning and counts as version 0; version 1 added only
    /// the stamp itself, so that upgrade rewrites nothing. Version 2 rebuilds the children index from
    /// every stored block, so a database that held blocks before the index existed answers child
    /// queries as complete. A newer version may hold key shapes this build does not know, so it is
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
    /// lists of deleted blocks) are dropped. The parent root is read at its fixed SSZ offset rather than
    /// decoded, so blocks of every fork rebuild alike. The routine is idempotent: it runs before the
    /// version stamp, and a crash in between only makes it run again.
    /// </remarks>
    private void RebuildChildrenIndex()
    {
        Dictionary<Hash256, byte[]> entries = [];
        foreach (KeyValuePair<byte[], byte[]> stored in _blocks.GetAll())
        {
            if (stored.Key.Length != Hash256.Size)
            {
                continue;
            }

            byte[] ssz = Snappy.DecompressToArray(stored.Value);
            if (ssz.Length < ParentRootOffset + Hash256.Size)
            {
                throw new InvalidOperationException($"The beaconChain database holds a {ssz.Length}-byte block record under {new Hash256(stored.Key)}, too short to be a signed beacon block; delete the beaconChain database to checkpoint-sync again.");
            }

            Hash256 root = new(stored.Key);
            Hash256 parentRoot = new(ssz.AsSpan(ParentRootOffset, Hash256.Size));

            byte[] ownEntry = entries.TryGetValue(root, out byte[]? existingOwn) ? existingOwn : NewChildrenEntry(ChildrenPending, parentRoot);
            ownEntry[0] = ChildrenComplete;
            parentRoot.Bytes.CopyTo(ownEntry.AsSpan(1));
            entries[root] = ownEntry;

            byte[] parentEntry = entries.TryGetValue(parentRoot, out byte[]? existingParent) ? existingParent : NewChildrenEntry(ChildrenPending, Hash256.Zero);
            if (!ContainsChild(parentEntry, root))
            {
                byte[] extended = new byte[parentEntry.Length + Hash256.Size];
                parentEntry.CopyTo(extended, 0);
                root.Bytes.CopyTo(extended.AsSpan(parentEntry.Length));
                parentEntry = extended;
            }

            entries[parentRoot] = parentEntry;
        }

        using IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
        IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
        foreach (byte[] key in _blockIndex.GetAllKeys())
        {
            if (key.Length == ChildrenKeyLength && key[0] == ChildrenKeyPrefix && !entries.ContainsKey(new Hash256(key.AsSpan(1))))
            {
                index.Remove(key);
            }
        }

        Span<byte> entryKey = stackalloc byte[ChildrenKeyLength];
        foreach (KeyValuePair<Hash256, byte[]> entry in entries)
        {
            ChildrenKey(entry.Key, entryKey);
            index.Set(entryKey, entry.Value);
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

    public void SetAnchor(Hash256 blockRoot, ulong slot)
    {
        byte[] value = new byte[AnchorValueLength];
        blockRoot.Bytes.CopyTo(value.AsSpan());
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(Hash256.Size), slot);
        PutMetadata(BeaconChainMetadataKeys.Anchor, value);
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
