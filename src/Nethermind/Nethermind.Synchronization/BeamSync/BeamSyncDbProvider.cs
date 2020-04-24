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
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamSyncDbProvider : IDbProvider
    {
        private readonly IDbProvider _otherProvider;
        public ISyncFeed<StateSyncBatch> BeamSyncFeed { get; }
        
        public BeamSyncDbProvider(ISyncModeSelector syncModeSelector, IDbProvider otherProvider, ILogManager logManager)
        {
            _otherProvider = otherProvider ?? throw new ArgumentNullException(nameof(otherProvider));
            BeamSyncDb codeDb = new BeamSyncDb(otherProvider.CodeDb.Innermost, otherProvider.BeamStateDb, syncModeSelector, logManager);
            BeamSyncDb stateDb = new BeamSyncDb(otherProvider.StateDb.Innermost, otherProvider.BeamStateDb, syncModeSelector, logManager);
            BeamSyncFeed = new CompositeStateSyncFeed<StateSyncBatch>(logManager,codeDb, stateDb);
            StateDb = new StateDb(stateDb);
            CodeDb = new StateDb(codeDb);
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IColumnsDb<ReceiptsColumns> ReceiptsDb => _otherProvider.ReceiptsDb;
        public IDb BlocksDb => _otherProvider.BlocksDb;
        public IDb HeadersDb => _otherProvider.HeadersDb;
        public IDb BlockInfosDb => _otherProvider.BlockInfosDb;
        public IDb PendingTxsDb => _otherProvider.PendingTxsDb;
        public IDb ConfigsDb => _otherProvider.ConfigsDb;
        public IDb EthRequestsDb => _otherProvider.EthRequestsDb;
        public IDb BloomDb => _otherProvider.BloomDb;
        public IDb BeamStateDb => _otherProvider.BeamStateDb;

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            ConfigsDb?.Dispose();
            EthRequestsDb?.Dispose();
            BloomDb?.Dispose();
        }
    }
}