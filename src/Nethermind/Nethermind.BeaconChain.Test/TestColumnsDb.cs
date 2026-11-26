// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.BeaconChain.Storage;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test;

internal abstract class TestColumnsDb : IColumnsDb<BeaconChainDbColumns>
{
    private readonly ConcurrentDictionary<BeaconChainDbColumns, IDb> _columns = [];

    public IDb GetColumnDb(BeaconChainDbColumns key) => _columns.GetOrAdd(key, CreateColumn);
    protected virtual IDb CreateColumn(BeaconChainDbColumns key) => new MemDb();
    public IEnumerable<BeaconChainDbColumns> ColumnKeys => Enum.GetValues<BeaconChainDbColumns>();
    public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);
    public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();
    public void Flush(bool onlyWal = false) { }
    public virtual void Dispose() { }
}
