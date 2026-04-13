// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class InMemoryColumnWriteBatch<TKey>(IColumnsDb<TKey> columnsDb) : IColumnsWriteBatch<TKey>
    {
        private readonly ConcurrentDictionary<TKey, IWriteBatch> _writeBatches = new();
        private readonly IColumnsDb<TKey> _columnsDb = columnsDb;

        public IWriteBatch GetColumnBatch(TKey key)
        {
            return _writeBatches.GetOrAdd(key, key => new InMemoryWriteBatch(_columnsDb.GetColumnDb(key)));
        }

        public void Clear()
        {
            foreach (IWriteBatch batch in _writeBatches.Values)
            {
                batch.Clear();
            }
        }

        public void Dispose()
        {
            foreach (IWriteBatch batch in _writeBatches.Values)
            {
                batch.Dispose();
            }
        }
    }
}
