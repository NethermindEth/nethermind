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

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Db
{
    // TODO: this is a copy paste from MemDbProvider (mainly commit / restore / snapshots), like most snapshotable classes, awaiting some refactoring
    public class RocksDbProvider : IDbProvider
    {
        private readonly ISnapshotableDb _stateDb;
        private readonly ISnapshotableDb _codeDb;

        public ISnapshotableDb GetOrCreateStateDb()
        {
            return _stateDb;
        }

        public ISnapshotableDb GetOrCreateCodeDb()
        {
            return _codeDb;
        }

        private readonly ILogger _logger;

        public RocksDbProvider(string dbBasePath, ILogger logger)
        {
            _logger = logger;
            _stateDb = new SnapshotableDb(new DbOnTheRocks(Path.Combine(dbBasePath, DbOnTheRocks.StateDbPath)));
            _codeDb = new SnapshotableDb(new DbOnTheRocks(Path.Combine(dbBasePath, DbOnTheRocks.CodeDbPath)));
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsDebugEnabled) _logger.Debug($"Restoring all DBs to {snapshot}");
            _stateDb.Restore(-1);
            _codeDb.Restore(-1);
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Committing all DBs");
            _stateDb.Commit(spec);
            _codeDb.Commit(spec);
        }

        public int TakeSnapshot()
        {
            return -1;
        }
    }
}