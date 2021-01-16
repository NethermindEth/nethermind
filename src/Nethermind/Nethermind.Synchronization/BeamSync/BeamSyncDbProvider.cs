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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamSyncDbProvider : IDbProvider
    {
        private readonly ConcurrentDictionary<string, IDb> _registeredDbs = new ConcurrentDictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase);
        private readonly IDbProvider _otherProvider;
        private BeamSyncDb _stateDb;
        private BeamSyncDb _codeDb;
        public ISyncFeed<StateSyncBatch?> BeamSyncFeed { get; }
        
        public BeamSyncDbProvider(ISyncModeSelector syncModeSelector, IDbProvider otherProvider, ISyncConfig syncConfig, ILogManager logManager)
        {
            _otherProvider = otherProvider ?? throw new ArgumentNullException(nameof(otherProvider));
            _codeDb = new BeamSyncDb(otherProvider.CodeDb.Innermost, otherProvider.BeamStateDb, syncModeSelector, logManager, syncConfig.BeamSyncContextTimeout, syncConfig.BeamSyncPreProcessorTimeout);
            _stateDb = new BeamSyncDb(otherProvider.StateDb.Innermost, otherProvider.BeamStateDb, syncModeSelector, logManager, syncConfig.BeamSyncContextTimeout, syncConfig.BeamSyncPreProcessorTimeout);
            BeamSyncFeed = new CompositeStateSyncFeed<StateSyncBatch?>(logManager, _codeDb, _stateDb);

            _registeredDbs.TryAdd(DbNames.Code, new StateDb(_codeDb)); // TODO: PRUNING - not state really
            _registeredDbs.TryAdd(DbNames.State, new StateDb(_stateDb)); // TODO: PRUNING - not state really
        }

        public void EnableVerifiedMode()
        {
            _stateDb.VerifiedModeEnabled = true;
            _codeDb.VerifiedModeEnabled = true;
        }
        
        public IDb BeamStateDb => _otherProvider.BeamStateDb;

        public DbModeHint DbMode => _otherProvider.DbMode;

        public IDictionary<string, IDb> RegisteredDbs
        {
            get
            {
                Dictionary<string, IDb > localDictionary = new Dictionary<string, IDb>();
                foreach (var (key, value) in _otherProvider.RegisteredDbs)
                {
                    if (_registeredDbs.ContainsKey(key))
                    {
                        localDictionary.Add(key, _registeredDbs[key]);
                    }
                    else
                    {
                        IDb? other = _otherProvider.RegisteredDbs[key];
                        localDictionary.Add(key, other);
                    }
                }

                return localDictionary;
            }   
        }

        public void Dispose()
        {
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            T? result;
            if (string.Equals(DbNames.Code, dbName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(DbNames.State, dbName, StringComparison.OrdinalIgnoreCase))
            {
                _registeredDbs.TryGetValue(dbName, out IDb? found);
                result = found as T;
                if (result == null && found != null)
                {
                    throw new IOException(
                        $"An attempt was made to resolve DB {dbName} as {typeof(T)} while its type is {found.GetType()}.");
                }
            }
            else
            {
                result = _otherProvider.GetDb<T>(dbName);    
            }

            if (result == null)
            {
                throw new IOException($"Database {dbName} cannot be found.");
            }
            
            return result;
        }

        public void RegisterDb<T>(string dbName, T db) where T : class, IDb
        {
            _otherProvider.RegisterDb<T>(dbName, db);
        }
    }
}
