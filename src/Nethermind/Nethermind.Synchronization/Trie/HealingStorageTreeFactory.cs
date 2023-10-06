// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingStorageTreeFactory : IStorageTreeFactory
{
    private ITrieNodeRecovery<GetTrieNodesRequest>? _recovery;

    public void InitializeNetwork(ITrieNodeRecovery<GetTrieNodesRequest> recovery)
    {
        _recovery = recovery;
    }

    public StorageTree Create(Address address, ITrieStore trieStore, Keccak storageRoot, Keccak stateRoot, ILogManager? logManager) =>
        new HealingStorageTree(trieStore, storageRoot, logManager, address, stateRoot, _recovery);
}
