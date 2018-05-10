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
using Nethermind.Store;

namespace Nethermind.Db
{
    // TODO: this is a copy paste, like most snapshotable classes, awaiting some refactoring
    public class RocksDbProvider : IDbProvider
    {
        private readonly ISnapshotableDb _stateDb = new SnapshotableDb(new DbOnTheRocks(DbOnTheRocks.StateDbPath));
        private readonly ISnapshotableDb _codeDb = new SnapshotableDb(new DbOnTheRocks(DbOnTheRocks.CodeDbPath));
        private readonly Dictionary<Address, ISnapshotableDb> _storageDbs = new Dictionary<Address, ISnapshotableDb>();
        private IEnumerable<ISnapshotableDb> AllDbs
        {
            get
            {
                yield return _stateDb;
                yield return _codeDb;
                foreach (ISnapshotableDb storageDb in _storageDbs.Values)
                {
                    yield return storageDb;
                }
            }
        }
        
        public ISnapshotableDb GetOrCreateStateDb()
        {
            return _stateDb;
        }

        public ISnapshotableDb GetOrCreateStorageDb(Address address)
        {
            if (!_storageDbs.ContainsKey(address))
            {
                _storageDbs[address] = new SnapshotableDb(new DbOnTheRocks(DbOnTheRocks.StorageDbPath, address.Hex));
            }

            return _storageDbs[address];
        }

        public ISnapshotableDb GetOrCreateCodeDb()
        {
            return _codeDb;
        }
        
        private readonly ILogger _logger;

        internal Stack<Dictionary<ISnapshotableDb, int>> Snapshots { get; } = new Stack<Dictionary<ISnapshotableDb, int>>();

        public RocksDbProvider(ILogger logger)
        {
            _logger = logger;
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsDebugEnabled) _logger.Debug($"Restoring all DBs to {snapshot}");

            while (Snapshots.Count != snapshot)
            {
                Snapshots.Pop();
            }

            Dictionary<ISnapshotableDb, int> dbSnapshots = Snapshots.Pop();
            foreach (ISnapshotableDb db in AllDbs)
            {
                db.Restore(dbSnapshots.ContainsKey(db) ? dbSnapshots[db] : -1);
            }
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Committing all DBs");

            foreach (ISnapshotableDb db in AllDbs)
            {
                db.Commit(spec);
            }

            Snapshots.Pop();
        }

        public int TakeSnapshot()
        {
            Dictionary<ISnapshotableDb, int> dbSnapshots = new Dictionary<ISnapshotableDb, int>();
            foreach (ISnapshotableDb db in AllDbs)
            {
                int dbSnapshot = db.TakeSnapshot();
                if (dbSnapshot == -1)
                {
                    continue;
                }

                dbSnapshots.Add(db, dbSnapshot);
            }

            Snapshots.Push(dbSnapshots);

            int snapshot = Snapshots.Count;
            if (_logger.IsDebugEnabled) _logger.Debug($"Taking DB snapshot at {snapshot}");
            return snapshot;
        }
    }
}