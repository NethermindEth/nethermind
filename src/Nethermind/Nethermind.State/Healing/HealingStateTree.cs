// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingStateTree : StateTree
{
    private IPathRecovery? _recovery;

    [DebuggerStepThrough]
    public HealingStateTree(ITrieStore? store, ILogManager? logManager)
        : base(store.GetTrieStore(null), logManager)
    {
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

    private bool Recover(in TreePath missingNodePath, in ValueHash256 hash, Hash256 fullPath)
    {
        if (_recovery is not null)
        {
            Console.Error.WriteLine($"recovring {missingNodePath} with hash {hash} and full path {fullPath}");
            using IOwnedReadOnlyList<(TreePath, byte[])>? rlps = _recovery.Recover(RootHash, null, missingNodePath, hash, fullPath, default).GetAwaiter().GetResult();
            if (rlps is not null)
            {
                foreach ((TreePath, byte[]) kv in rlps)
                {
                    ValueHash256 nodeHash = ValueKeccak.Compute(kv.Item2);
                    TrieStore.Set(kv.Item1, nodeHash, kv.Item2);
                    Console.Error.WriteLine($"recovered {kv.Item1}, {kv.Item2.ToHexString()}");
                }
                return true;
            }
        }

        return false;
    }
}
