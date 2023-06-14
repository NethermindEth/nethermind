// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                logger.Received(1).Info("Old Headers    0 / 100 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
                logger.Received(1).Info("Old Bodies     0 / 70 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
                logger.Received(1).Info("Old Receipts   0 / 65 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
            }
            else
            {
                logger.Received(1).Info("Old Headers    0 / 100 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
                logger.Received(1).Info("Old Bodies     0 / 100 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
                logger.Received(1).Info("Old Receipts   0 / 100 (  0.00 %) | queue         0 | current         0.00 Blk/s | total         0.00 Blk/s");
            }
        }
    }
}
