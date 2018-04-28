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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public class DbProvider : IDbProvider
    {
        private readonly IDb _stateDb = new InMemoryDb();
        private readonly IDb _codeDb = new InMemoryDb();
        private readonly Dictionary<Address, IDb> _storageDbs = new Dictionary<Address, IDb>();
        private IEnumerable<IDb> AllDbs
        {
            get
            {
                yield return _stateDb;
                yield return _codeDb;
                foreach (IDb storageDb in _storageDbs.Values)
                {
                    yield return storageDb;
                }
            }
        }
        
        public IDb GetOrCreateStateDb()
        {
            return _stateDb;
        }

        public IDb GetOrCreateStorageDb(Address address)
        {
            if (!_storageDbs.ContainsKey(address))
            {
                _storageDbs[address] = new InMemoryDb();
            }

            return _storageDbs[address];
        }

        public IDb GetOrCreateCodeDb()
        {
            return _codeDb;
        }
        
        private readonly ILogger _logger;

        private readonly Stack<Dictionary<IDb, int>> _snapshots = new Stack<Dictionary<IDb, int>>();

        public DbProvider(ILogger logger)
        {
            _logger = logger;
            _snapshots.Push(new Dictionary<IDb, int>());
        }

        public void Restore(int snapshot)
        {
            if(_logger.IsDebugEnabled) _logger.Debug($"Restoring all DBs to {snapshot}");

            while (_snapshots.Count - 2 != snapshot)
            {
                _snapshots.Pop();
            }

            Dictionary<IDb, int> dbSnapshots = _snapshots.Peek();
            foreach (IDb db in AllDbs)
            {
                db.Restore(dbSnapshots.ContainsKey(db) ? dbSnapshots[db] : -1);
            }
        }

        public void Commit(IReleaseSpec spec)
        {
            if(_logger.IsDebugEnabled) _logger.Debug("Committing all DBs");

            foreach (IDb db in AllDbs)
            {
                db.Commit(spec);
            }
        }

        public int TakeSnapshot()
        {
            Dictionary<IDb, int> dbSnapshots = new Dictionary<IDb, int>();
            foreach (IDb db in AllDbs)
            {
                dbSnapshots.Add(db, db.TakeSnapshot());
            }

            _snapshots.Push(dbSnapshots);

            int snapshot = _snapshots.Count - 2;
            if(_logger.IsDebugEnabled) _logger.Debug($"Taking DB snapshot at {snapshot}");
            return snapshot;
        }
    }
}