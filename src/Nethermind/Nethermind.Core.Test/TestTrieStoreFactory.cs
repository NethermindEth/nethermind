// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

// Note: Prefer `RawScopedTrieStore` where possible as it is constructed faster.
public static class TestTrieStoreFactory
{
    private static IPruningConfig _testPruningConfig = new PruningConfig()
    {
        Mode = PruningMode.Full,
        DirtyNodeShardBit = 1,
    };

    public static TestRawTrieStore Build(INodeStorage nodeStorage, ILogManager logManager)
    {
        return new TestRawTrieStore(nodeStorage);
    }

    public static TestRawTrieStore Build(IKeyValueStoreWithBatching keyValueStore, ILogManager logManager)
    {
        return Build(new NodeStorage(keyValueStore), logManager);
    }

    public static TrieStore Build(IKeyValueStoreWithBatching keyValueStore, IPruningStrategy pruningStrategy, IPersistenceStrategy persistenceStrategy, ILogManager logManager)
    {
        return new TrieStore(new NodeStorage(keyValueStore), pruningStrategy, persistenceStrategy, _testPruningConfig, logManager);
    }
}
