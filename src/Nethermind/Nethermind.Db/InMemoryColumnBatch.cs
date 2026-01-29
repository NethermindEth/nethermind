// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using NonBlocking;

namespace Nethermind.Db
{
    public class InMemoryColumnWriteBatch<TKey> : IColumnsWriteBatch<TKey>
    {
        private readonly ConcurrentDictionary<TKey, IWriteBatch> _writeBatches = new();
        private readonly IColumnsDb<TKey> _columnsDb;

        public InMemoryColumnWriteBatch(IColumnsDb<TKey> columnsDb)
        {
            _columnsDb = columnsDb;
        }

        public IWriteBatch GetColumnBatch(TKey key)
        {
            return _writeBatches.GetOrAdd(key, key => new InMemoryWriteBatch(_columnsDb.GetColumnDb(key)));
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
