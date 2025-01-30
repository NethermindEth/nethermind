// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingStorageTree : StorageTree
{
    private readonly Address _address;
    private readonly Hash256 _stateRoot;
    private readonly IPathRecovery? _recovery;

    public HealingStorageTree(IScopedTrieStore? trieStore, Hash256 rootHash, ILogManager? logManager, Address address, Hash256 stateRoot, IPathRecovery? recovery)
        : base(trieStore, rootHash, logManager)
    {
        _address = address;
        _stateRoot = stateRoot;
        _recovery = recovery;
    }

    public override ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
    {
        try
        {
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            Hash256 fullPath = new Hash256(rawKey);
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
            Hash256 fullPath = new Hash256(rawKey);
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

    private bool Recover(in TreePath missingNodePath, in ValueHash256 hash, Hash256 fullPath)
    {
        if (_recovery is not null)
        {
            using IOwnedReadOnlyList<(TreePath, byte[])>? rlps = _recovery.Recover(_stateRoot, ValueKeccak.Compute(_address.Bytes), missingNodePath, hash, fullPath, default).GetAwaiter().GetResult();
            if (rlps is not null)
            {
                foreach ((TreePath, byte[]) kv in rlps)
                {
                    ValueHash256 nodeHash = ValueKeccak.Compute(kv.Item2);
                    TrieStore.Set(kv.Item1, nodeHash, kv.Item2);
                }
                return true;
            }
        }

        return false;
    }
}
