// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class InMemoryColumnBatch<TKey> : IColumnsBatch<TKey>
    {
        private IList<IBatch> _underlyingBatch = new List<IBatch>();
        private readonly IColumnsDb<TKey> _columnsDb;

        public InMemoryColumnBatch(IColumnsDb<TKey> columnsDb)
        {
            _columnsDb = columnsDb;
        }

        public IBatch GetColumnBatch(TKey key)
        {
            InMemoryBatch batch = new InMemoryBatch(_columnsDb.GetColumnDb(key));
            _underlyingBatch.Add(batch);
            return batch;
        }

        public void Dispose()
        {
            foreach (IBatch batch in _underlyingBatch)
            {
                batch.Dispose();
            }
        }
    }
}
