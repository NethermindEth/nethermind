// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class InMemoryColumnWriteBatch<TKey>(IColumnsDb<TKey> columnsDb) : IColumnsWriteBatch<TKey>
    {
        private readonly ConcurrentDictionary<TKey, IWriteBatch> _writeBatches = new();
        private readonly IColumnsDb<TKey> _columnsDb = columnsDb;

        public IWriteBatch GetColumnBatch(TKey key) => _writeBatches.GetOrAdd(key, key => new InMemoryWriteBatch(_columnsDb.GetColumnDb(key)));

        public void Clear()
        {
            foreach (KeyValuePair<TKey, IWriteBatch> kvp in _writeBatches)
            {
                kvp.Value.Clear();
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<TKey, IWriteBatch> kvp in _writeBatches)
            {
                kvp.Value.Dispose();
            }
        }
    }
}
