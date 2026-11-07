// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>A <see cref="TrieStoreScopeProvider"/> over a witness that rejects a trie whose paths do not end at the length
/// of its hashed keys.</summary>
/// <remarks>Walks the state trie as a scope opens, and notes each storage tree's root to walk once the tree loads it; see
/// <see cref="WitnessTriePaths"/>.</remarks>
internal sealed class WitnessTrieStoreScopeProvider(ITrieStore trieStore, WitnessTriePaths paths, IKeyValueStoreWithBatching codeDb, ILogManager logManager)
    : TrieStoreScopeProvider(trieStore, codeDb, UnavailableStateHeaderProvider.Instance, logManager)
{
    protected override bool CanBeginScope(BlockHeader? baseBlock)
    {
        if (!base.CanBeginScope(baseBlock)) return false;

        paths.ValidateStateTrie(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);
        return true;
    }

    protected override StorageTree CreateStorageTree(Address address, Hash256 storageRoot)
    {
        paths.AddStorageRoot(storageRoot);
        return base.CreateStorageTree(address, storageRoot);
    }
}
