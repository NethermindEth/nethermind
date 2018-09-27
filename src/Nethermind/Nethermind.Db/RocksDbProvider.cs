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
using System.IO;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Db.Config;
using Nethermind.Store;

namespace Nethermind.Db
{
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

        public RocksDbProvider(string dbBasePath, ILogManager logManager, IDbConfig dbConfig)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _stateDb = new SnapshotableDb(new DbOnTheRocks(Path.Combine(dbBasePath, DbOnTheRocks.StateDbPath), dbConfig));
            _codeDb = new SnapshotableDb(new DbOnTheRocks(Path.Combine(dbBasePath, DbOnTheRocks.CodeDbPath), dbConfig));
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring all DBs to {snapshot}");
            _stateDb.Restore(-1);
            _codeDb.Restore(-1);
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_logger.IsTrace) _logger.Trace("Committing all DBs");
            _stateDb.Commit(spec);
            _codeDb.Commit(spec);
        }

        public int TakeSnapshot()
        {
            return -1;
        }

        public void Dispose()
        {
            _stateDb?.Dispose();
            _codeDb?.Dispose();
        }
    }
}