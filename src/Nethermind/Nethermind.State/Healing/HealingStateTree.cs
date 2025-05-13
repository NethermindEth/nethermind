// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingStateTree : StateTree
{
    private IPathRecovery? _recovery;
    private readonly INodeStorage _nodeStorage;

    [DebuggerStepThrough]
    public HealingStateTree(ITrieStore? store, INodeStorage nodeStorage, ILogManager? logManager)
        : base(store.GetTrieStore(null), logManager)
    {
        _nodeStorage = nodeStorage;
    }

    public void InitializeNetwork(IPathRecovery recovery)
    {
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

    private bool Recover(in TreePath missingNodePath, Hash256 hash, Hash256 fullPath)
    {
        if (_recovery is not null)
        {
            using IOwnedReadOnlyList<(TreePath, byte[])>? rlps = _recovery.Recover(RootHash, null, missingNodePath, hash, fullPath).GetAwaiter().GetResult();
            if (rlps is not null)
            {
                foreach ((TreePath, byte[]) kv in rlps)
                {
                    ValueHash256 nodeHash = ValueKeccak.Compute(kv.Item2);
                    _nodeStorage.Set(null, kv.Item1, nodeHash, kv.Item2);
                }
                return true;
            }
        }

        return false;
    }
}
