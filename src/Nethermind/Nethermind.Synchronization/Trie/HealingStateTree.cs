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

    public override byte[]? Get(ReadOnlySpan<byte> rawKey, Keccak? rootHash = null)
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

    private bool Recover(in ValueKeccak rlpHash, ReadOnlySpan<byte> pathPart, Keccak rootHash)
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
                TrieStore.AsKeyValueStore().Set(rlpHash.Bytes, rlp);
                return true;
            }
        }

        return false;
    }
}
