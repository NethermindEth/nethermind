//  Copyright (c) 2021 Demerzel Solutions Limited
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
        private readonly ConcurrentDictionary<string, IDb> _registeredDbs = new (StringComparer.InvariantCultureIgnoreCase);
        private readonly IDbProvider _otherProvider;
        private readonly BeamSyncDb _stateDb;
        private readonly BeamSyncDb _codeDb;
        private readonly MemDb _beamTempDb = new (); // holds items that have been beam synced but cannot be persisted yet
        public ISyncFeed<StateSyncBatch?> BeamSyncFeed { get; }
        
        public BeamSyncDbProvider(ISyncModeSelector syncModeSelector, IDbProvider? otherProvider, ISyncConfig syncConfig, ILogManager logManager)
        {
            _otherProvider = otherProvider ?? throw new ArgumentNullException(nameof(otherProvider));
            _codeDb = new BeamSyncDb(otherProvider.CodeDb.Innermost, BeamTempDb, syncModeSelector, logManager, syncConfig.BeamSyncContextTimeout, syncConfig.BeamSyncPreProcessorTimeout);
            _stateDb = new BeamSyncDb(otherProvider.StateDb.Innermost, BeamTempDb, syncModeSelector, logManager, syncConfig.BeamSyncContextTimeout, syncConfig.BeamSyncPreProcessorTimeout);
            
            // we first take out the innermost B from the current state DBs
            // then we wrap them inside a BeamSyncDB that is capable of going to the network to retrieve nodes
            BeamSyncFeed = new CompositeStateSyncFeed<StateSyncBatch?>(logManager, _codeDb, _stateDb);
 
            // then we wrap the beam sync DB back in a StateDb to make it snapshottable
            _registeredDbs.TryAdd(DbNames.Code, _codeDb);
            _registeredDbs.TryAdd(DbNames.State, _stateDb);
        }

        public void EnableVerifiedMode()
        {
            _stateDb.VerifiedModeEnabled = true;
            _codeDb.VerifiedModeEnabled = true;
        }

        public IDb BeamTempDb => _beamTempDb;

        public DbModeHint DbMode => _otherProvider.DbMode;

        public IDictionary<string, IDb> RegisteredDbs
        {
            get
            {
                Dictionary<string, IDb > localDictionary = new ();
                foreach (var (key, _) in _otherProvider.RegisteredDbs)
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

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            T? result;
            if (string.Equals(DbNames.Code, dbName, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(DbNames.State, dbName, StringComparison.InvariantCultureIgnoreCase))
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
        
        public void Dispose()
        {
            _otherProvider.Dispose();
            _codeDb.Dispose();
            _stateDb.Dispose();
            _beamTempDb.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
