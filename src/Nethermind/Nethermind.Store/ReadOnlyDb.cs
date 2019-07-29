/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nethermind.Store
{
    public class ReadOnlyDb : IDb
    {
        private MemDb _memDb = new MemDb();

        private readonly IDb _wrappedDb;
        private readonly bool _createInMemWriteStore;

        public ReadOnlyDb(IDb wrappedDb, bool createInMemWriteStore)
        {
            _wrappedDb = wrappedDb;
            _createInMemWriteStore = createInMemWriteStore;
        }

        public void Dispose()
        {
        }

        public string Name { get; } = "ReadOnlyDb";

        public byte[] this[byte[] key]
        {
            get => _memDb[key] ?? _wrappedDb[key];
            set
            {
                if (!_createInMemWriteStore)
                {
                    throw new InvalidOperationException($"This {nameof(ReadOnlyDb)} did not expect any writes.");
                }

                _memDb[key] = value;
            }
        }

        public byte[][] GetAll() => _memDb.GetAll();

        public void StartBatch()
        {
        }

        public void CommitBatch()
        {
        }

        public void Remove(byte[] key)
        {
        }

        public bool KeyExists(byte[] key)
        {
            return _memDb.KeyExists(key) || _wrappedDb.KeyExists(key);
        }

        public void ClearTempChanges()
        {
            _memDb.Clear();
        }
    }
}