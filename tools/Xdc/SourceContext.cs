// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Xdc;

public sealed class SourceContext : IDisposable
{
    public Hash256 StateRoot { get; }
    public IDb Db { get; }
    public StateTree StateTree { get; }

    public SourceContext(IDb db, Hash256 stateRoot)
    {
        Db = db;
        StateRoot = stateRoot;

        var trieStore = new ReadOnlyScopedHashTrieStore(Db);
        StateTree = new(trieStore, NullLogManager.Instance) { RootHash = stateRoot };
    }

    public void Dispose()
    {
        Db.Dispose();
    }
}
