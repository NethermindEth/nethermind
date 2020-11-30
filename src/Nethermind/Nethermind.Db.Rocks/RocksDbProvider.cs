//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace Nethermind.Db.Rocks
{
    public class RocksDbProvider : IDbProvider
    {
        private readonly Dictionary<string, IDb> _registeredDbs = new Dictionary<string, IDb>();

        public RocksDbProvider(DbModeHint dbMode)
        {
            DbMode = dbMode;
        }
        public IDb BeamStateDb { get; } = new MemDb();

        public DbModeHint DbMode { get; }

        public void Dispose()
        {
            if (_registeredDbs != null)
            {
                foreach (var registeredDb in _registeredDbs)
                {
                    registeredDb.Value?.Dispose();
                }
            }
        }

        public T GetDb<T>(string dbName) where T : IDb
        {
            if (!_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} wasn't registed.");
            }

            return (T)_registeredDbs[dbName];
        }

        public void RegisterDb<T>(string dbName, T db) where T : IDb
        {
            if (_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} has already registered.");
            }

            _registeredDbs.Add(dbName, db);
        }
    }
}
