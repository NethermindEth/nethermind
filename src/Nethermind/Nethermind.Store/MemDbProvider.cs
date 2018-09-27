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
using System.Collections.Generic;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public class MemDbProvider : IDbProvider
    {
        private readonly ISnapshotableDb _stateDb = new SnapshotableDb(new MemDb());
        private readonly ISnapshotableDb _codeDb = new SnapshotableDb(new MemDb());

        public ISnapshotableDb GetOrCreateStateDb()
        {
            return _stateDb;
        }

        public ISnapshotableDb GetOrCreateCodeDb()
        {
            return _codeDb;
        }

        private readonly ILogger _logger;

        internal Stack<Dictionary<ISnapshotableDb, int>> Snapshots { get; } = new Stack<Dictionary<ISnapshotableDb, int>>();

        public MemDbProvider(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsDebug) _logger.Debug($"Restoring all DBs to {snapshot}");

            while (Snapshots.Count != snapshot)
            {
                Snapshots.Pop();
            }

            Dictionary<ISnapshotableDb, int> dbSnapshots = Snapshots.Pop();

            _stateDb.Restore(dbSnapshots.ContainsKey(_stateDb) ? dbSnapshots[_stateDb] : -1);
            _codeDb.Restore(dbSnapshots.ContainsKey(_codeDb) ? dbSnapshots[_codeDb] : -1);
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_logger.IsDebug) _logger.Debug("Committing all DBs");
            _stateDb.Commit(spec);
            _codeDb.Commit(spec);
            Snapshots.Pop();
        }

        public int TakeSnapshot()
        {
            Dictionary<ISnapshotableDb, int> dbSnapshots = new Dictionary<ISnapshotableDb, int>();
            dbSnapshots.Add(_stateDb, _stateDb.TakeSnapshot());
            dbSnapshots.Add(_codeDb, _codeDb.TakeSnapshot());
            Snapshots.Push(dbSnapshots);

            int snapshot = Snapshots.Count;
            if (_logger.IsDebug) _logger.Debug($"Taking DB snapshot at {snapshot}");
            return snapshot;
        }

        public void Dispose()
        {
            _stateDb?.Dispose();
            _codeDb?.Dispose();
        }
    }
}