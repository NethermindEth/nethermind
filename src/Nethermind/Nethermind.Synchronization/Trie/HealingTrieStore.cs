// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

/// <summary>
/// Trie store that can recover from network using eth63-eth66 protocol and GetNodeData.
/// </summary>
public class HealingTrieStore : TrieStore
{
    private readonly ILogManager? _logManager;
    public bool Throw { get; set; }
    private GetNodeDataTrieNodeRecovery? _recovery;

    public HealingTrieStore(
        IKeyValueStoreWithBatching? keyValueStore,
        IPruningStrategy? pruningStrategy,
        IPersistenceStrategy? persistenceStrategy,
        ILogManager? logManager)
        : base(keyValueStore, pruningStrategy, persistenceStrategy, logManager)
    {
        _logManager = logManager;
    }

    public void InitializeNetwork(ISyncPeerPool syncPeerPool)
    {
        _recovery = new GetNodeDataTrieNodeRecovery(syncPeerPool, _logManager);
    }

    public override byte[] LoadRlp(Keccak keccak, ReadFlags readFlags = ReadFlags.None)
    {
        try
        {
            // For test only!
            if (Throw)
            {
                Throw = false;
                throw new TrieException("Artificial exception");
            }

            return base.LoadRlp(keccak, readFlags);
        }
        catch (TrieException)
        {
            if (BlockchainProcessor.IsMainProcessingThread && TryRecover(keccak, out byte[] rlp))
            {
                return rlp;
            }

            throw;
        }
    }

    private bool TryRecover(Keccak keccak, [NotNullWhen(true)] out byte[]? rlp)
    {
        rlp = _recovery?.Recover(keccak).GetAwaiter().GetResult();
        if (rlp is null) return false;
        _keyValueStore.Set(keccak.Bytes, rlp);
        return true;
    }
}
