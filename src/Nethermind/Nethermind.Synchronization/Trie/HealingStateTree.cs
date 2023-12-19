// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingStateTree : StateTree
{
    private ITrieNodeRecovery<GetTrieNodesRequest>? _recovery;

    [DebuggerStepThrough]
    public HealingStateTree(ITrieStore? store, ILogManager? logManager)
        : base(store, logManager)
    {
    }

    public void InitializeNetwork(ITrieNodeRecovery<GetTrieNodesRequest> recovery)
    {
        _recovery = recovery;
    }

    public override byte[]? Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
    {
        try
        {
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            if (Recover(e.TrieNodeException.NodeHash, e.GetPathPart(), rootHash ?? RootHash))
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
            if (Recover(e.TrieNodeException.NodeHash, e.GetPathPart(), RootHash))
            {
                base.Set(rawKey, value);
            }
            else
            {
                throw;
            }
        }
    }

    private bool Recover(in ValueHash256 rlpHash, ReadOnlySpan<byte> pathPart, Hash256 rootHash)
    {
        if (_recovery?.CanRecover == true)
        {
            GetTrieNodesRequest request = new()
            {
                RootHash = rootHash,
                AccountAndStoragePaths = new[]
                {
                    new PathGroup
                    {
                        Group = new[] { Nibbles.EncodePath(pathPart) }
                    }
                }
            };

            byte[]? rlp = _recovery.Recover(rlpHash, request).GetAwaiter().GetResult();
            if (rlp is not null)
            {
                TrieStore.Set(rlpHash, rlp);
                return true;
            }
        }

        return false;
    }
}
