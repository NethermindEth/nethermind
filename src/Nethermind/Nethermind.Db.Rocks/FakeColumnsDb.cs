// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Db.Rocks;

public class FakeColumnsDb<T>(
    Dictionary<T, IDb> innerDb
): IColumnsDb<T> where T : notnull
{
    public void Flush(bool onlyWal = false)
    {
        foreach (var keyValuePair in innerDb)
        {
            keyValuePair.Value.Flush(onlyWal);
        }
    }

    public void Dispose()
    {
    }

    public IDb GetColumnDb(T key)
    {
        return innerDb[key];
    }

    public IEnumerable<T> ColumnKeys => innerDb.Keys;
    public IColumnsWriteBatch<T> StartWriteBatch()
    {
        return new FakeWriteBatch(innerDb);
    }

    public IColumnDbSnapshot<T> CreateSnapshot()
    {
        return new FakeSnapshot(innerDb.ToDictionary((kv) => kv.Key, (kv) =>
        {
            return ((ISnapshottableKeyValueStore)kv.Value).CreateSnapshot();
        }));
    }

    private class FakeWriteBatch : IColumnsWriteBatch<T>
    {
        private Dictionary<T, IWriteBatch> _innerWriteBatch;

        public FakeWriteBatch(Dictionary<T, IDb> innerDb)
        {
            _innerWriteBatch = innerDb
                .ToDictionary((kv) => kv.Key, (kv) => kv.Value.StartWriteBatch());
        }

        public IWriteBatch GetColumnBatch(T key)
        {
            return _innerWriteBatch[key];
        }

        public void Dispose()
        {
            foreach (var keyValuePair in _innerWriteBatch)
            {
                keyValuePair.Value.Dispose();
            }
        }
    }

    private class FakeSnapshot(Dictionary<T, IDbSnapshot> innerDb) : IColumnDbSnapshot<T>
    {

        public IReadOnlyKeyValueStore GetColumn(T key)
        {
            return innerDb[key];
        }

        public void Dispose()
        {
            foreach (var keyValuePair in innerDb)
            {
                keyValuePair.Value.Dispose();
            }
        }
    }
}
