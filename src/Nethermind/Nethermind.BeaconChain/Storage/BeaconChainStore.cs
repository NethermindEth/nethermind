// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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
}

/// <summary>Persistence for beacon blocks, states, the canonical slot index, and driver metadata.</summary>
/// <remarks>
/// Blocks and states are stored as snappy-compressed SSZ. States are large (hundreds of MB), so
/// they are split into <see cref="StateChunkSize"/> uncompressed slices compressed independently,
/// keyed by <c>blockRoot ++ big-endian chunk index</c>, with a manifest (chunk count and
/// uncompressed length) under the bare block root.
/// </remarks>
public class BeaconChainStore(IColumnsDb<BeaconChainDbColumns> db)
{
    private const int StateChunkSize = 4 * 1024 * 1024;
    private const int StateManifestLength = sizeof(uint) + sizeof(ulong);
    private const int AnchorValueLength = Hash256.Size + sizeof(ulong);

    private readonly IDb _blocks = db.GetColumnDb(BeaconChainDbColumns.Blocks);
    private readonly IDb _blockIndex = db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
    private readonly IDb _states = db.GetColumnDb(BeaconChainDbColumns.States);
    private readonly IDb _metadata = db.GetColumnDb(BeaconChainDbColumns.Metadata);

    public void PutBlock(Hash256 root, SignedBeaconBlock block) =>
        _blocks.Set(root.Bytes, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

    public bool TryGetBlock(Hash256 root, [NotNullWhen(true)] out SignedBeaconBlock? block)
    {
        byte[]? compressed = _blocks.Get(root.Bytes);
        if (compressed is null)
        {
            block = null;
            return false;
        }

        SignedBeaconBlock.Decode(Snappy.DecompressToArray(compressed), out block);
        return true;
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
