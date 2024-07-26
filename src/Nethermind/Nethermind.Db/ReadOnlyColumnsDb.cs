// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Db
{
    public class ReadOnlyColumnsDb<T> : IReadOnlyColumnDb<T>, IDisposable
    {
        private readonly IDictionary<T, IReadOnlyDb> _readOnlyColumns;

        public ReadOnlyColumnsDb(IColumnsDb<T> baseColumnDb, bool createInMemWriteStore)
        {
            _readOnlyColumns = baseColumnDb.ColumnKeys
                .Select(key => (key, baseColumnDb.GetColumnDb(key).CreateReadOnly(createInMemWriteStore)))
                .ToDictionary(it => it.Item1, it => it.Item2);
        }

        public IDb GetColumnDb(T key)
        {
            return _readOnlyColumns[key!];
        }

        public IEnumerable<T> ColumnKeys => _readOnlyColumns.Keys;
        public IColumnsWriteBatch<T> StartWriteBatch()
        {
            return new InMemoryColumnWriteBatch<T>(this);
        }

        public void ClearTempChanges()
        {
            foreach (KeyValuePair<T, IReadOnlyDb> readOnlyColumn in _readOnlyColumns)
            {
                readOnlyColumn.Value.ClearTempChanges();
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<T, IReadOnlyDb> readOnlyColumn in _readOnlyColumns)
            {
                readOnlyColumn.Value.Dispose();
            }
        }
    }
}
