// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
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
        private static Block CreateBlockWithTimestamp(DateTimeOffset timestamp)
        {
            return new Block(new BlockHeader(
                Nethermind.Core.Crypto.Keccak.Zero,
                Nethermind.Core.Crypto.Keccak.Zero,
                Address.Zero,
                0,
                0,
                0,
                (ulong)timestamp.ToUnixTimeSeconds(),
                []));
        }

        [Test]
        public void Smoke(
            [Values(true, false)]
            bool fastSync)
        {
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);

            Queue<SyncMode> syncModes = new();
            syncModes.Enqueue(SyncMode.WaitingForBlock);
            syncModes.Enqueue(SyncMode.FastSync);
            syncModes.Enqueue(SyncMode.Full);
            syncModes.Enqueue(SyncMode.FastBlocks);
            syncModes.Enqueue(SyncMode.StateNodes);
            syncModes.Enqueue(SyncMode.Disconnected);

            SyncConfig syncConfig = new()
            {
                FastSync = fastSync,
            };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), LimboLogs.Instance, timerFactory);

            void UpdateMode()
            {
                syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, syncModes.Count > 0 ? syncModes.Dequeue() : SyncMode.Full));
            }

            timer.Elapsed += Raise.Event();
            UpdateMode();
            syncReport.FastBlocksHeaders.MarkEnd();
            UpdateMode();
            syncReport.FastBlocksBodies.MarkEnd();
            UpdateMode();
            syncReport.FastBlocksReceipts.MarkEnd();
            timer.Elapsed += Raise.Event();
        }

        [Test]
        public void Ancient_bodies_and_receipts_are_reported_correctly()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsError.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger<SyncReport>().Returns(logger);
            logManager.GetClassLogger<ProgressLogger>().Returns(logger);

            Queue<SyncMode> syncModes = new();
            syncModes.Enqueue(SyncMode.FastHeaders);
            syncModes.Enqueue(SyncMode.FastBodies);
            syncModes.Enqueue(SyncMode.FastReceipts);

            SyncConfig syncConfig = new()
            {
                FastSync = true,
                PivotNumber = 100,
            };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), logManager, timerFactory);
            syncReport.FastBlocksHeaders.Reset(0, 100);
            syncReport.FastBlocksHeaders.CurrentQueued = 0;
            syncReport.FastBlocksBodies.Reset(0, 70);
            syncReport.FastBlocksBodies.CurrentQueued = 0;
            syncReport.FastBlocksReceipts.Reset(0, 65);
            syncReport.FastBlocksReceipts.CurrentQueued = 0;
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.FastHeaders | SyncMode.FastBodies | SyncMode.FastReceipts));
            timer.Elapsed += Raise.Event();

            iLogger.Received(1).Info("Old Headers           0 /        100 (  0.00 %) [                                     ] queue        0 | current       0 Blk/s");
            iLogger.Received(1).Info("Old Bodies            0 /         70 (  0.00 %) [                                     ] queue        0 | current       0 Blk/s");
            iLogger.Received(1).Info("Old Receipts          0 /         65 (  0.00 %) [                                     ] queue        0 | current       0 Blk/s");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Ancient_bodies_and_receipts_are_not_reported_until_feed_finishes_Initialization(bool setBarriers)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsError.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger<SyncReport>().Returns(logger);
            logManager.GetClassLogger<ProgressLogger>().Returns(logger);

            Queue<SyncMode> syncModes = new();
            syncModes.Enqueue(SyncMode.FastHeaders);
            syncModes.Enqueue(SyncMode.FastBodies);
            syncModes.Enqueue(SyncMode.FastReceipts);

            SyncConfig syncConfig = new()
            {
                FastSync = true,
                PivotNumber = 100,
            };
            if (setBarriers)
            {
                syncConfig.AncientBodiesBarrier = 30;
                syncConfig.AncientReceiptsBarrier = 35;
            }

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.FastHeaders | SyncMode.FastBodies | SyncMode.FastReceipts));
            timer.Elapsed += Raise.Event();

            if (setBarriers)
            {
                iLogger.DidNotReceive().Info("Old Headers    0 / 100 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
                iLogger.DidNotReceive().Info("Old Bodies     0 / 70 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
                iLogger.DidNotReceive().Info("Old Receipts   0 / 65 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
            }
            else
            {
                iLogger.DidNotReceive().Info("Old Headers    0 / 100 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
                iLogger.DidNotReceive().Info("Old Bodies     0 / 100 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
                iLogger.DidNotReceive().Info("Old Receipts   0 / 100 (  0.00 %) | queue         0 | current            0 Blk/s | total            0 Blk/s");
            }
        }
        [Test]
        public void Sync_behind_warning_is_logged_when_head_is_behind()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            // Set up a head block with a timestamp 10 minutes behind current time
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddMinutes(-10)));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.Full));

            timer.Elapsed += Raise.Event();

            iLogger.Received().Warn(Arg.Is<string>(s => s.Contains("Node is behind the head of the chain by")));
        }

        [Test]
        public void Sync_behind_warning_is_logged_in_waiting_for_block_mode()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            // Simulate blackout recovery: head is 2 hours behind, mode is WaitingForBlock (PoS beacon control)
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddHours(-2)));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.WaitingForBlock));

            timer.Elapsed += Raise.Event();

            iLogger.Received().Warn(Arg.Is<string>(s => s.Contains("Node is behind the head of the chain by")));
        }

        [Test]
        public void Sync_behind_warning_is_not_logged_when_head_is_close()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            // Set up a head block with a timestamp only 2 minutes behind (under threshold)
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddMinutes(-2)));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.Full));

            timer.Elapsed += Raise.Event();

            iLogger.DidNotReceive().Warn(Arg.Any<string>());
        }

        [Test]
        public void Sync_behind_warning_is_not_logged_when_not_in_forward_sync()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            // Head is 10 minutes behind, but sync mode is FastHeaders (not Full/FastSync/WaitingForBlock)
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddMinutes(-10)));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.FastHeaders));

            timer.Elapsed += Raise.Event();

            iLogger.DidNotReceive().Warn(Arg.Any<string>());
        }

        [Test]
        public void Sync_behind_warning_is_not_logged_when_head_timestamp_is_zero()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            // Genesis block with timestamp 0
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UnixEpoch));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.Full));

            timer.Elapsed += Raise.Event();

            iLogger.DidNotReceive().Warn(Arg.Any<string>());
        }

        [Test]
        public void Caught_up_info_is_logged_after_being_behind()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
            pool.InitializedPeersCount.Returns(1);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            ILogManager logManager = Substitute.For<ILogManager>();
            InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
            iLogger.IsInfo.Returns(true);
            iLogger.IsWarn.Returns(true);
            ILogger logger = new(iLogger);
            logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            // Start 10 minutes behind to trigger the warning
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddMinutes(-10)));

            SyncConfig syncConfig = new() { FastSync = true };

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), blockFinder, logManager, timerFactory);
            syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, SyncMode.Full));

            // First tick (reportId=0, 0%6==0): should warn about being behind
            timer.Elapsed += Raise.Event();
            iLogger.Received().Warn(Arg.Is<string>(s => s.Contains("Node is behind the head of the chain by")));

            // Now the node catches up — head is only 1 minute behind
            blockFinder.Head.Returns(CreateBlockWithTimestamp(DateTimeOffset.UtcNow.AddMinutes(-1)));
            iLogger.ClearReceivedCalls();

            // Advance to the next warning tick (reportId=6, 6%6==0)
            for (int i = 0; i < 6; i++)
                timer.Elapsed += Raise.Event();

            iLogger.Received().Info(Arg.Is<string>(s => s.Contains("Node has caught up with the head of the chain")));
        }
    }
}
