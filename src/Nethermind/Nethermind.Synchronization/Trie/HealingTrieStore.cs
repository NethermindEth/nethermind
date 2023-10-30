// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    private ITrieNodeRecovery<IReadOnlyList<Hash256>>? _recovery;

    public HealingTrieStore(
        IKeyValueStoreWithBatching? keyValueStore,
        IPruningStrategy? pruningStrategy,
        IPersistenceStrategy? persistenceStrategy,
        ILogManager? logManager)
        : base(keyValueStore, pruningStrategy, persistenceStrategy, logManager)
    {
    }

    public void InitializeNetwork(ITrieNodeRecovery<IReadOnlyList<Hash256>> recovery)
    {
        _recovery = recovery;
    }

    public override byte[] LoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        try
        {
            return base.LoadRlp(keccak, readFlags);
        }
        catch (TrieNodeException)
        {
            if (TryRecover(keccak, out byte[] rlp))
            {
                return rlp;
            }

            throw;
        }
    }

    private bool TryRecover(Hash256 rlpHash, [NotNullWhen(true)] out byte[]? rlp)
    {
        if (_recovery?.CanRecover == true)
        {
            using ArrayPoolList<Hash256> request = new(1) { rlpHash };
            rlp = _recovery.Recover(rlpHash, request).GetAwaiter().GetResult();
            if (rlp is not null)
            {
                _keyValueStore.Set(rlpHash.Bytes, rlp);
                return true;
            }
        }

        rlp = null;
        return false;
    }
}
