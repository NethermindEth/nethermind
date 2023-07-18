// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

/// <summary>
/// Trie store that can recover from network using eth63-eth66 protocol and GetNodeData.
/// </summary>
public class HealingTrieStore : TrieStore
{
    public bool Throw { get; set; }
    private ITrieNodeRecovery<IReadOnlyList<Keccak>>? _recovery;

    public HealingTrieStore(
        IKeyValueStoreWithBatching? keyValueStore,
        IPruningStrategy? pruningStrategy,
        IPersistenceStrategy? persistenceStrategy,
        ILogManager? logManager)
        : base(keyValueStore, pruningStrategy, persistenceStrategy, logManager)
    {
    }

    public void InitializeNetwork(ITrieNodeRecovery<IReadOnlyList<Keccak>> recovery)
    {
        _recovery = recovery;
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
        using ArrayPoolList<Keccak> request = new(1) { keccak };
        rlp = _recovery?.Recover(request).GetAwaiter().GetResult();
        if (rlp is null) return false;
        _keyValueStore.Set(keccak.Bytes, rlp);
        return true;
    }
}
