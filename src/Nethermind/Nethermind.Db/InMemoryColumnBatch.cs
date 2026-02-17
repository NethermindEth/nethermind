// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class InMemoryColumnWriteBatch<TKey> : IColumnsWriteBatch<TKey>
    {
        private readonly IList<IWriteBatch> _underlyingBatch = new List<IWriteBatch>();
        private readonly IColumnsDb<TKey> _columnsDb;

        public InMemoryColumnWriteBatch(IColumnsDb<TKey> columnsDb)
        {
            _columnsDb = columnsDb;
        }

        public IWriteBatch GetColumnBatch(TKey key)
        {
            InMemoryWriteBatch writeBatch = new InMemoryWriteBatch(_columnsDb.GetColumnDb(key));
            _underlyingBatch.Add(writeBatch);
            return writeBatch;
        }

        public void Dispose()
        {
            foreach (IWriteBatch batch in _underlyingBatch)
            {
                batch.Dispose();
            }
        }
    }
}
