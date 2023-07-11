// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public static class TrieNodeResolverCapabilityStateExtension
{
    public static IStateTree CreateStateStore(this TrieNodeResolverCapability capability)
    {
        return capability switch
        {
            TrieNodeResolverCapability.Hash => new StateTree(),
            TrieNodeResolverCapability.Path => new StateTreeByPath(),
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    public static IStateTree CreateStateStore(this TrieNodeResolverCapability capability, ITrieStore? store, ITrieStore? storageStore, ILogManager? logManager)
    {
        return capability switch
        {
            TrieNodeResolverCapability.Hash => new StateTree(store, logManager),
            TrieNodeResolverCapability.Path => new StateTreeByPath(store, logManager),
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    public static IStateTree CreateStateStore(this TrieNodeResolverCapability capability,  IColumnsDb<StateColumns>? db, ILogManager? logManager)
    {
        return capability switch
        {
            TrieNodeResolverCapability.Hash => new StateTree(capability.CreateTrieStore(db, logManager), logManager),
            TrieNodeResolverCapability.Path => new StateTreeByPath(capability.CreateTrieStore(db, logManager), logManager),
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }
}
