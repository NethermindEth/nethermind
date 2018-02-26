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

namespace Nethermind.Store
{
    public class MultiDb : IMultiDb
    {
        private readonly List<IDb> _dbs = new List<IDb>();
        private readonly ILogger _logger;

        private readonly Stack<Dictionary<IDb, int>> _snapshots = new Stack<Dictionary<IDb, int>>();

        public MultiDb(ILogger logger)
        {
            _logger = logger;
            _snapshots.Push(new Dictionary<IDb, int>());
        }

        public void Restore(int snapshot)
        {
            _logger?.Log($"RESTORING ALL DBS TO {snapshot}");

            while (_snapshots.Count - 2 != snapshot)
            {
                _snapshots.Pop();
            }

            Dictionary<IDb, int> dbSnapshots = _snapshots.Peek();
            foreach (IDb db in _dbs)
            {
                db.Restore(dbSnapshots.ContainsKey(db) ? dbSnapshots[db] : -1);
            }
        }

        public void Commit()
        {
            _logger?.Log("COMMITING ALL DBS");

            foreach (IDb db in _dbs)
            {
                db.Commit();
            }
        }

        public int TakeSnapshot()
        {
            Dictionary<IDb, int> dbSnapshots = new Dictionary<IDb, int>();
            foreach (IDb db in _dbs)
            {
                dbSnapshots.Add(db, db.TakeSnapshot());
            }

            _snapshots.Push(dbSnapshots);

            int snapshot = _snapshots.Count - 2;
            _logger?.Log($"TAKING DBS SNAPSHOT AT {snapshot}");
            return snapshot;
        }

        public IDb CreateDb()
        {
            IDb db = new InMemoryDb();
            _dbs.Add(db);
            return db;
        }
    }
}