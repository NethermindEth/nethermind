// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public sealed class HealingStorageTree(
    IScopedTrieStore? trieStore,
    INodeStorage nodeStorage,
    Hash256 rootHash,
    ILogManager? logManager,
    Address address,
    Hash256 stateRoot,
    Lazy<IPathRecovery> recovery)
    : StorageTree(trieStore, rootHash, logManager)
{
    public override ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
    {
        try
        {
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            Hash256 fullPath = new(rawKey);
            if (Recover(e.Path, e.Hash, fullPath))
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
            Hash256 fullPath = new(rawKey);
            if (Recover(e.Path, e.Hash, fullPath))
            {
                base.Set(rawKey, value);
            }
            else
            {
                throw;
            }
        }
    }

    private bool Recover(in TreePath missingNodePath, Hash256 hash, Hash256 fullPath)
    {
        if (recovery is not null)
        {
            using IOwnedReadOnlyList<(TreePath, byte[])>? rlps = recovery.Value.Recover(stateRoot, Keccak.Compute(address.Bytes), missingNodePath, hash, fullPath).GetAwaiter().GetResult();
            if (rlps is not null)
            {
                Hash256 addressHash = address.ToAccountPath.ToCommitment();
                foreach ((TreePath, byte[]) kv in rlps)
                {
                    ValueHash256 nodeHash = ValueKeccak.Compute(kv.Item2);
                    nodeStorage.Set(addressHash, kv.Item1, nodeHash, kv.Item2);
                }
                return true;
            }
        }

        return false;
    }
}
