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

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SyncReportTest
    {
        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void Smoke(bool fastSync, bool fastBlocks)
        {
            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            
            Queue<SyncMode> _syncModes = new Queue<SyncMode>();
            _syncModes.Enqueue(SyncMode.None);
            _syncModes.Enqueue(SyncMode.FastSync);
            _syncModes.Enqueue(SyncMode.Full);
            _syncModes.Enqueue(SyncMode.FastBlocks);
            _syncModes.Enqueue(SyncMode.StateNodes);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastBlocks = fastBlocks;
            syncConfig.FastSync = fastSync;
            
            SyncReport syncReport = new SyncReport(pool, Substitute.For<INodeStatsManager>(), selector,  syncConfig, LimboLogs.Instance, 10);
            selector.Current.Returns((ci) => _syncModes.Count > 0 ? _syncModes.Dequeue() : SyncMode.Full);
            Thread.Sleep(200);
            syncReport.FastBlocksHeaders.MarkEnd();
            syncReport.FastBlocksBodies.MarkEnd();
            syncReport.FastBlocksReceipts.MarkEnd();
            Thread.Sleep(20);
        }
    }
}