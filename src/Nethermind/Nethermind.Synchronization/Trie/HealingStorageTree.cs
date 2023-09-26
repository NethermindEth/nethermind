// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingStorageTree : StorageTree
{
    private readonly Address _address;
    private readonly Keccak _stateRoot;
    private readonly ITrieNodeRecovery<GetTrieNodesRequest>? _recovery;

    public HealingStorageTree(ITrieStore? trieStore, Keccak rootHash, ILogManager? logManager, Address address, Keccak stateRoot, ITrieNodeRecovery<GetTrieNodesRequest>? recovery)
        : base(trieStore, rootHash, logManager)
    {
        _address = address;
        _stateRoot = stateRoot;
        _recovery = recovery;
    }

    public override byte[]? Get(ReadOnlySpan<byte> rawKey, Keccak? rootHash = null)
    {
        try
        {
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            if (Recover(e.TrieNodeException.NodeHash, e.GetPathPart()))
            {
                return base.Get(rawKey, rootHash);
            }

            throw;
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
            if (Recover(e.TrieNodeException.NodeHash, e.GetPathPart()))
            {
                base.Set(rawKey, value);
            }
            else
            {
                throw;
            }
        }
    }

    private bool Recover(in ValueKeccak rlpHash, ReadOnlySpan<byte> pathPart)
    {
        if (_recovery?.CanRecover == true)
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

            byte[]? rlp = _recovery.Recover(rlpHash, request).GetAwaiter().GetResult();
            if (rlp is not null)
            {
                TrieStore.AsKeyValueStore().Set(rlpHash.Bytes, rlp);
                return true;
            }
        }

        return false;
    }
}
