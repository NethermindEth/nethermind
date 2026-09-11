// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Core.Test;

// Note: Prefer `RawScopedTrieStore` where possible as it is constructed faster.
public static class TestTrieStoreFactory
{
    public static TestRawTrieStore Build(INodeStorage nodeStorage, ILogManager logManager) => new(nodeStorage);

    public static TestRawTrieStore Build(IKeyValueStoreWithBatching keyValueStore, ILogManager logManager) => Build(new TestNodeStorage(keyValueStore), logManager);
}
