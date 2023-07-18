// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingStorageTree : StorageTree
{
    private readonly Address _address;
    private readonly Keccak _stateRoot;
    private readonly SnapTrieNodeRecovery? _recovery;

    public HealingStorageTree(IBlockTree blockTree, ITrieStore? trieStore, Keccak rootHash, ILogManager? logManager, Address address, Keccak stateRoot, ISyncPeerPool? syncPeerPool)
        : base(trieStore, rootHash, logManager)
    {
        _address = address;
        _stateRoot = stateRoot;
        if (syncPeerPool is not null)
        {
            _recovery = new SnapTrieNodeRecovery(syncPeerPool, blockTree, logManager);
        }
    }

    public override byte[]? Get(ReadOnlySpan<byte> rawKey, Keccak? rootHash = null)
    {
        try
        {
            // if (Throw)
            // {
            //     Throw = false;
            //     byte[] nibbles = new byte[rawKey.Length * 2];
            //     Nibbles.BytesToNibbleBytes(rawKey, nibbles);
            //     throw new MissingTrieNodeException("Test", null!, nibbles, 1);
            // }
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            Throw = false;
            if (BlockchainProcessor.IsMainProcessingThread && Recover(e.GetPathPart()))
            {
                return base.Get(rawKey, rootHash);
            }
            else
            {
                _logger.Error("Get recovery failed!");
                return base.Get(rawKey, rootHash);
                // throw;
            }
        }
    }

    public override void Set(ReadOnlySpan<byte> rawKey, byte[] value)
    {
        try
        {
            base.Set(rawKey, value);
        }
        catch (MissingTrieNodeException e)
        {
            Throw = false;
            if (BlockchainProcessor.IsMainProcessingThread && Recover(e.GetPathPart()))
            {
                base.Set(rawKey, value);
            }
            else
            {
                _logger.Error("Set recovery failed!");
                base.Set(rawKey, value);
            }
        }
    }

    private bool Recover(ReadOnlySpan<byte> pathPart)
    {
        GetTrieNodesRequest request = new()
        {
            RootHash = _stateRoot,
            AccountAndStoragePaths = new[]
            {
                new PathGroup
                {
                    Group = new[] { ValueKeccak.Compute(_address.Bytes).ToByteArray(), Nibbles.EncodePath(pathPart) }
                }
            }
        };


        byte[]? rlp = _recovery?.Recover(request).GetAwaiter().GetResult();
        if (rlp is null)
        {
            _logger.Error($"Recovery of {pathPart.ToHexString()} failed");
            return false;
        }
        TrieStore.Set(ValueKeccak.Compute(rlp).Bytes, rlp);
        _logger.Error($"Recovery of {pathPart.ToHexString()} succeeded");
        return true;
    }
}
