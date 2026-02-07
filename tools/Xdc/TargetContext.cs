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
    public IDb SnapshotDb { get; }

    public IScopedTrieStore TrieStore { get; }
    public StateTree StateTree { get; }

    public TargetContext(IDbFactory dbFactory, Hash256 stateRoot)
    {
        StateDb = dbFactory.CreateDb(new(DbNames.State, "state"));
        CodeDb = dbFactory.CreateDb(new(DbNames.Code, "code"));
        SnapshotDb = dbFactory.CreateDb(new("Snapshots", "snapshots"));
        TrieStore = new RawScopedTrieStore(new NodeStorage(dbFactory.CreateDb(new(DbNames.State, "state")), INodeStorage.KeyScheme.Hash, requirePath: false));
        StateTree = new StateTree(TrieStore, NullLogManager.Instance) {RootHash = stateRoot};
    }

    public void Dispose()
    {
        StateDb.Dispose();
        CodeDb.Dispose();
        SnapshotDb.Dispose();
    }
}
