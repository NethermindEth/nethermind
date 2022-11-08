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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class SyncReportTest
    {
        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public async Task Smoke(bool fastSync, bool fastBlocks)
        {
            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);

            Queue<SyncMode> _syncModes = new();
            _syncModes.Enqueue(SyncMode.WaitingForBlock);
            _syncModes.Enqueue(SyncMode.FastSync);
            _syncModes.Enqueue(SyncMode.Full);
            _syncModes.Enqueue(SyncMode.FastBlocks);
            _syncModes.Enqueue(SyncMode.StateNodes);
            _syncModes.Enqueue(SyncMode.Disconnected);

            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = fastBlocks;
            syncConfig.FastSync = fastSync;

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), selector, syncConfig, Substitute.For<IPivot>(), LimboLogs.Instance, timerFactory);
            selector.Current.Returns((ci) => _syncModes.Count > 0 ? _syncModes.Dequeue() : SyncMode.Full);
            timer.Elapsed += Raise.Event();
            syncReport.FastBlocksHeaders.MarkEnd();
            syncReport.FastBlocksBodies.MarkEnd();
            syncReport.FastBlocksReceipts.MarkEnd();
            timer.Elapsed += Raise.Event();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Ancient_bodies_and_receipts_are_reported_correctly(bool setBarriers)
        {
            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            ILogger logger = Substitute.For<ILogger>();
            logger.IsInfo.Returns(true);
            logManager.GetClassLogger().Returns(logger);

            Queue<SyncMode> _syncModes = new();
            _syncModes.Enqueue(SyncMode.FastHeaders);
            _syncModes.Enqueue(SyncMode.FastBodies);
            _syncModes.Enqueue(SyncMode.FastReceipts);

            SyncConfig syncConfig = new();
            syncConfig.FastBlocks = true;
            syncConfig.FastSync = true;
            syncConfig.PivotNumber = "100";
            if (setBarriers)
            {
                syncConfig.AncientBodiesBarrier = 30;
                syncConfig.AncientReceiptsBarrier = 35;
            }

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), selector, syncConfig, Substitute.For<IPivot>(), logManager, timerFactory);
            selector.Current.Returns(_ => SyncMode.FastHeaders | SyncMode.FastBodies | SyncMode.FastReceipts);
            timer.Elapsed += Raise.Event();

            if (setBarriers)
            {
                logger.Received(1).Info("Old Headers    0 / 100 | queue     0 | current     0.00bps | total     0.00bps");
                logger.Received(1).Info("Old Bodies     0 / 70 | queue     0 | current     0.00bps | total     0.00bps");
                logger.Received(1).Info("Old Receipts   0 / 65 | queue     0 | current     0.00bps | total     0.00bps");
            }
            else
            {
                logger.Received(1).Info("Old Headers    0 / 100 | queue     0 | current     0.00bps | total     0.00bps");
                logger.Received(1).Info("Old Bodies     0 / 100 | queue     0 | current     0.00bps | total     0.00bps");
                logger.Received(1).Info("Old Receipts   0 / 100 | queue     0 | current     0.00bps | total     0.00bps");
            }
        }
    }
}
