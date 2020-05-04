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

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamSyncDbProvider : IDbProvider
    {
        public INodeDataConsumer NodeDataConsumer { get; }
        
        public BeamSyncDbProvider(IDbProvider otherProvider, string description, ILogManager logManager)
        {
            BeamSyncDb codeDb = new BeamSyncDb(otherProvider.CodeDb.Innermost, logManager);
            BeamSyncDb stateDb = new BeamSyncDb(otherProvider.StateDb.Innermost, logManager);
            NodeDataConsumer = new CompositeDataConsumer(logManager,codeDb, stateDb);
            BlocksDb = otherProvider.BlocksDb;
            HeadersDb = otherProvider.HeadersDb;
            BlockInfosDb = otherProvider.BlockInfosDb;
            StateDb = new StateDb(stateDb);
            CodeDb = new StateDb(codeDb);
            PendingTxsDb = otherProvider.PendingTxsDb;
            ConfigsDb = otherProvider.ConfigsDb;
            EthRequestsDb = otherProvider.EthRequestsDb;
            ReceiptsDb = otherProvider.ReceiptsDb;
            BloomDb = otherProvider.BloomDb;
            ChtDb = otherProvider.ChtDb;
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IColumnsDb<ReceiptsColumns> ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb HeadersDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }
        public IDb ConfigsDb { get; }
        public IDb EthRequestsDb { get; }
        public IDb BloomDb { get; }
        public IDb ChtDb { get; }

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
            ChtDb?.Dispose();
        }
    }
}