// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Storage;

public partial class BeaconChainStore
{
    private const uint StateRootIndexSchemaVersion = 6;
    private const byte StateRootKeyPrefix = 4;
    private const byte BlockStateKeyPrefix = 3;
    private const int StateRootOffset = ParentRootOffset + Hash256.Size;

    internal bool TryGetBlockRootByStateRoot(Hash256 stateRoot, out Hash256? blockRoot)
    {
        Span<byte> key = stackalloc byte[1 + Hash256.Size];
        StateRootKey(stateRoot.Bytes, key);
        byte[]? value = _blockIndex.Get(key);
        blockRoot = value is { Length: Hash256.Size } ? new Hash256(value) : null;
        return blockRoot is not null && HasBlock(blockRoot);
    }

    private static void StateRootKey(ReadOnlySpan<byte> stateRoot, Span<byte> key)
    {
        key[0] = StateRootKeyPrefix;
        stateRoot.CopyTo(key[1..]);
    }

    private static void PutStateRootIndex(IWriteBatch index, Hash256 root, ReadOnlySpan<byte> ssz)
    {
        // Beacon API params/index.yaml StateId names the state commitment carried by the block header.
        Span<byte> key = stackalloc byte[1 + Hash256.Size];
        StateRootKey(ssz.Slice(StateRootOffset, Hash256.Size), key);
        index.PutSpan(key, root.Bytes);
        StateRootKey(root.Bytes, key);
        key[0] = BlockStateKeyPrefix;
        index.PutSpan(key, ssz.Slice(StateRootOffset, Hash256.Size));
    }

    private void RemoveStateRootIndex(IWriteBatch index, Hash256 root)
    {
        Span<byte> key = stackalloc byte[1 + Hash256.Size];
        StateRootKey(root.Bytes, key);
        key[0] = BlockStateKeyPrefix;
        byte[]? stateRoot = _blockIndex.Get(key);
        index.Remove(key);
        if (stateRoot is not { Length: Hash256.Size }) return;
        StateRootKey(stateRoot, key);
        if (_blockIndex.Get(key) is { } value && value.AsSpan().SequenceEqual(root.Bytes)) index.Remove(key);
    }

    private void RebuildStateRootIndex()
    {
        Span<byte> prefix = stackalloc byte[StateRootOffset + Hash256.Size];
        Span<byte> key = stackalloc byte[1 + Hash256.Size];
        IColumnsWriteBatch<BeaconChainDbColumns>? batch = null;
        int count = 0;
        try
        {
            foreach (byte[] blockKey in _blocks.GetAllKeys())
            {
                if (blockKey.Length != Hash256.Size || _blocks.Get(blockKey) is not { } compressed) continue;
                if (DecompressSnappyPrefix(compressed, prefix, ReqRespFraming.MaxPayloadSize) < prefix.Length)
                    throw new InvalidDataException("Stored beacon block is too short to contain its state commitment.");
                StateRootKey(prefix[StateRootOffset..], key);
                batch ??= db.StartWriteBatch();
                IWriteBatch index = batch.GetColumnBatch(BeaconChainDbColumns.BlockIndex);
                index.Set(key, blockKey);
                StateRootKey(blockKey, key);
                key[0] = BlockStateKeyPrefix;
                index.PutSpan(key, prefix[StateRootOffset..]);
                if (++count == ChildrenRebuildBatchSize)
                {
                    batch.Dispose();
                    batch = null;
                    count = 0;
                }
            }
        }
        finally
        {
            batch?.Dispose();
        }
    }
}
