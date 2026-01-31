// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Xdc;

public sealed class TargetContext: IDisposable
{
    public IDb StateDb { get; }
    public IDb CodeDb { get; }
    public IScopedTrieStore TrieStore { get; }
    public StateTree StateTree { get; }

    public TargetContext(IDb stateDb, IDb codeDb, Hash256 stateRoot)
    {
        StateDb = stateDb;
        CodeDb = codeDb;
        TrieStore = new RawScopedTrieStore(new NodeStorage(stateDb, INodeStorage.KeyScheme.Hash, requirePath: false));
        StateTree = new StateTree(TrieStore, NullLogManager.Instance) {RootHash = stateRoot};
    }

    public void Dispose()
    {
        StateDb.Dispose();
        CodeDb.Dispose();
    }
}
