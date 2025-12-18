// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingStorageTreeFactory(INodeStorage nodeStorage, Lazy<IPathRecovery> recovery) : IStorageTreeFactory
{
    public StorageTree Create(Address address, IScopedTrieStore trieStore, Hash256 storageRoot, Hash256 stateRoot, ILogManager? logManager) =>
        new HealingStorageTree(trieStore, nodeStorage, storageRoot, logManager, address, stateRoot, recovery);
}
