// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

// Note: Prefer `RawScopedTrieStore` where possible as it is constructed faster.
public static class TestTrieStoreFactory
{
    public static TrieStore Build(INodeStorage nodeStorage, ILogManager logManager)
    {
        return new TrieStore(nodeStorage, No.Pruning, Persist.EveryBlock, logManager);
    }

    public static TrieStore Build(IKeyValueStoreWithBatching keyValueStore, ILogManager logManager)
    {
        return Build(new NodeStorage(keyValueStore), logManager);
    }

    public static TrieStore Build(IKeyValueStoreWithBatching keyValueStore, IPruningStrategy pruningStrategy, IPersistenceStrategy persistenceStrategy, ILogManager? logManager)
    {
        return new TrieStore(new NodeStorage(keyValueStore), pruningStrategy, persistenceStrategy, logManager);
    }
}
