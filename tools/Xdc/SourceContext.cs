// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Xdc;

namespace Xdc;

public sealed class SourceContext : IDisposable
{
    public IDb Db { get; }
    public XdcReader Reader { get; }
    public StateTree StateTree { get; }

    public XdcBlockHeader Pivot { get; }
    public Hash256 StateRoot => Pivot.StateRoot!;

    public SourceContext(IDb db, XdcReader reader, XdcBlockHeader pivot)
    {
        Db = db;
        Reader = reader;
        Pivot = pivot;

        var trieStore = new ReadOnlyScopedHashTrieStore(Db);
        StateTree = new(trieStore, NullLogManager.Instance)
        {
            RootHash = Pivot.StateRoot ?? throw new Exception("Failed to get head state root.")
        };
    }

    public void Dispose()
    {
        Db.Dispose();
    }
}
