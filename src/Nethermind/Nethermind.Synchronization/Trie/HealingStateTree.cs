// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingStateTree : StateTree
{
    private readonly ILogManager? _logManager;
    private SnapTrieNodeRecovery? _recovery;

    [DebuggerStepThrough]
    public HealingStateTree(ITrieStore? store, ILogManager? logManager)
        : base(store, logManager)
    {
        _logManager = logManager;
    }

    public bool Throw { get; set; }

    public void InitializeNetwork(ISyncPeerPool syncPeerPool)
    {
        _recovery = new SnapTrieNodeRecovery(syncPeerPool, _logManager);
    }

    public override byte[]? Get(ReadOnlySpan<byte> rawKey, Keccak? rootHash = null)
    {
        try
        {
            if (Throw)
            {
                Throw = false;
                byte[] nibbles = new byte[rawKey.Length * 2];
                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                throw new MissingTrieNodeException("Test", null!, nibbles, rawKey.Length * 2 - 1);
            }
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException e)
        {
            if (BlockchainProcessor.IsMainProcessingThread && Recover(e.GetPathPart(), rootHash ?? RootHash))
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
            if (BlockchainProcessor.IsMainProcessingThread && Recover(e.GetPathPart(), RootHash))
            {
                base.Set(rawKey, value);
            }

            throw;
        }
    }

    private bool Recover(ReadOnlySpan<byte> pathPart, Keccak rootHash)
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

        byte[]? rlp = _recovery?.Recover(request).GetAwaiter().GetResult();
        if (rlp is null) return false;
        TrieStore.Set(ValueKeccak.Compute(rlp).Bytes, rlp);
        return true;
    }
}
