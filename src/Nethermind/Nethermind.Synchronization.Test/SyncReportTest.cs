// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.State;
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

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), Substitute.For<ITimestamper>(), new BlocksConfig(), LimboLogs.Instance, timerFactory);

            void UpdateMode() =>
                syncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, syncModes.Count > 0 ? syncModes.Dequeue() : SyncMode.Full));

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

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), Substitute.For<ITimestamper>(), new BlocksConfig(), logManager, timerFactory);
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

        [Test]
        public void Ancient_bodies_and_receipts_are_not_reported_until_feed_finishes_Initialization([Values] bool setBarriers)
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

            SyncReport syncReport = new(pool, Substitute.For<INodeStatsManager>(), syncConfig, Substitute.For<IPivot>(), Substitute.For<IBlockFinder>(), Substitute.For<ITimestamper>(), new BlocksConfig(), logManager, timerFactory);
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

        private static readonly DateTime SyncBehindNow = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private const ulong DefaultSecondsPerSlot = 12;

        private const string BehindMessage = "Node is behind the head of the chain by";
        private const string CaughtUpMessage = "Node has caught up with the head of the chain";
        private const string EtaMessage = "Estimated time to catch up";
        private const string NotClosingMessage = "The gap is not closing.";

        private static ulong SyncBehindThreshold(ulong secondsPerSlot) => SyncReport.SyncBehindThresholdSlots * secondsPerSlot;

        private static IEnumerable<TestCaseData> SyncBehindCases()
        {
            yield return new TestCaseData(SyncBehindThreshold(DefaultSecondsPerSlot) + 1, SyncMode.Full, DefaultSecondsPerSlot) { ExpectedResult = true, TestName = "Just past the threshold in full sync" };
            yield return new TestCaseData(10 * 60UL, SyncMode.Full, DefaultSecondsPerSlot) { ExpectedResult = true, TestName = "Behind in full sync" };
            yield return new TestCaseData(10 * 60UL, SyncMode.FastSync, DefaultSecondsPerSlot) { ExpectedResult = true, TestName = "Behind in fast sync" };
            // Blackout recovery: the CL feeds missed blocks via engine_newPayload, which leaves the
            // node in beacon-controlled WaitingForBlock rather than Full or FastSync.
            yield return new TestCaseData(2 * 60 * 60UL, SyncMode.WaitingForBlock, DefaultSecondsPerSlot) { ExpectedResult = true, TestName = "Behind in waiting for block" };
            yield return new TestCaseData(SyncBehindThreshold(DefaultSecondsPerSlot), SyncMode.Full, DefaultSecondsPerSlot) { ExpectedResult = false, TestName = "Exactly at the threshold" };
            yield return new TestCaseData(2 * 60UL, SyncMode.Full, DefaultSecondsPerSlot) { ExpectedResult = false, TestName = "Within the threshold" };
            yield return new TestCaseData(10 * 60UL, SyncMode.FastHeaders, DefaultSecondsPerSlot) { ExpectedResult = false, TestName = "Behind but not in a forward sync mode" };
            yield return new TestCaseData(SyncBehindThreshold(2) + 1, SyncMode.Full, 2UL) { ExpectedResult = true, TestName = "Just past the threshold with short slots" };
            yield return new TestCaseData(10 * 60UL, SyncMode.Full, 30UL) { ExpectedResult = false, TestName = "Within the threshold with long slots" };
        }

        [TestCaseSource(nameof(SyncBehindCases))]
        public bool Sync_behind_is_reported_only_past_the_threshold_during_forward_sync(ulong secondsBehind, SyncMode syncMode, ulong secondsPerSlot)
        {
            using SyncBehindHarness harness = new(syncMode, secondsPerSlot);
            harness.SetHeadBehindBy(secondsBehind);

            harness.Tick();

            return harness.Reported(BehindMessage);
        }

        [Test]
        public void Sync_behind_is_reported_once_per_interval_regardless_of_tick_length([Values] bool isDebug)
        {
            using SyncBehindHarness harness = new(SyncMode.Full, isDebug: isDebug);
            harness.SetHeadBehindBy(10 * 60);

            harness.Tick();
            Assert.That(harness.Reported(BehindMessage), Is.True);

            harness.Logger.ClearReceivedCalls();
            for (TimeSpan elapsed = harness.TickInterval; elapsed < SyncReport.SyncBehindReportInterval; elapsed += harness.TickInterval)
            {
                harness.Clock.Add(harness.TickInterval);
                harness.Tick();
            }

            Assert.That(harness.Reported(BehindMessage), Is.False);

            harness.Clock.Add(harness.TickInterval);
            harness.Tick();

            Assert.That(harness.Reported(BehindMessage), Is.True);
        }

        [TestCase(false, TestName = "No head")]
        [TestCase(true, TestName = "Genesis head")]
        public void Sync_behind_is_not_reported_without_a_meaningful_head(bool hasGenesisHead)
        {
            using SyncBehindHarness harness = new(SyncMode.Full);
            harness.BlockFinder.Head.Returns(hasGenesisHead ? Build.A.Block.Genesis.WithTimestamp(1).TestObject : null);

            harness.Tick();

            Assert.That(harness.Reported(BehindMessage), Is.False);
        }

        [Test]
        public void Sync_behind_escalates_to_warning_only_after_the_node_reached_the_tip()
        {
            using SyncBehindHarness harness = new(SyncMode.Full);
            harness.SetHeadBehindBy(10 * 60);

            harness.Tick();

            using (Assert.EnterMultipleScope())
            {
                harness.Logger.Received().Info(Arg.Is<string>(s => s.Contains(BehindMessage)));
                harness.Logger.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains(BehindMessage)));
            }

            // Reach the tip, then fall behind again - now it is a regression worth warning about.
            harness.SetHeadBehindBy(0);
            harness.TickToNextReport();
            harness.SetHeadBehindBy(10 * 60);
            harness.Logger.ClearReceivedCalls();
            harness.TickToNextReport();

            harness.Logger.Received().Warn(Arg.Is<string>(s => s.Contains(BehindMessage)));
        }

        [Test]
        public void Caught_up_is_reported_once_after_being_behind()
        {
            using SyncBehindHarness harness = new(SyncMode.Full);

            // Starting at the tip leaves nothing to recover from.
            harness.SetHeadBehindBy(12);
            harness.Tick();
            Assert.That(harness.Reported(CaughtUpMessage), Is.False);

            harness.SetHeadBehindBy(10 * 60);
            harness.TickToNextReport();
            Assert.That(harness.Reported(BehindMessage), Is.True);

            // Back within the reporting threshold but not yet near the tip: neither behind nor caught up.
            harness.SetHeadBehindBy(2 * 60);
            harness.Logger.ClearReceivedCalls();
            harness.TickToNextReport();
            Assert.That(harness.Reported(BehindMessage) || harness.Reported(CaughtUpMessage), Is.False);

            harness.SetHeadBehindBy(12);
            harness.TickToNextReport();
            Assert.That(harness.Reported(CaughtUpMessage), Is.True);

            // Staying at the tip must not repeat it.
            harness.Logger.ClearReceivedCalls();
            harness.TickToNextReport();

            Assert.That(harness.Reported(CaughtUpMessage), Is.False);
        }

        [TestCase(SyncMode.WaitingForBlock, 60UL, 6 * 60UL, EtaMessage + ": 3m 0s", TestName = "Head advancing faster than the clock")]
        [TestCase(SyncMode.WaitingForBlock, 60UL, 60UL, NotClosingMessage, TestName = "Head advancing as fast as the clock")]
        [TestCase(SyncMode.WaitingForBlock, 60UL, 30UL, NotClosingMessage, TestName = "Head advancing slower than the clock")]
        [TestCase(SyncMode.WaitingForBlock, 3 * 60UL, 6 * 60UL, null, TestName = "Previous sample too old")]
        [TestCase(SyncMode.FastSync | SyncMode.StateNodes, 60UL, 0UL, null, TestName = "Head held back by state sync")]
        public void Sync_behind_reports_catch_up_eta_from_head_progress_since_the_previous_report(SyncMode syncMode, ulong secondsBetweenReports, ulong headAdvanceSeconds, string? expected)
        {
            const ulong initialSecondsBehind = 20 * 60;

            using SyncBehindHarness harness = new(syncMode);
            harness.SetHeadBehindBy(initialSecondsBehind);

            harness.Tick();
            Assert.That(harness.Reported(EtaMessage) || harness.Reported(NotClosingMessage), Is.False, "no estimate before a second sample");

            harness.Clock.Add(TimeSpan.FromSeconds(secondsBetweenReports));
            harness.SetHeadBehindBy(initialSecondsBehind + secondsBetweenReports - headAdvanceSeconds);
            harness.Logger.ClearReceivedCalls();
            harness.Tick();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Reported(BehindMessage), Is.True);
                Assert.That(expected is null ? harness.Reported(EtaMessage) || harness.Reported(NotClosingMessage) : !harness.Reported(expected), Is.False);
            }
        }

        /// <summary>Drives the production-wired <see cref="SyncReport"/> against a manual clock and a stubbed head.</summary>
        private sealed class SyncBehindHarness : IDisposable
        {
            private readonly IContainer _container;
            private readonly ITimer _timer = Substitute.For<ITimer>();

            internal SyncReport SyncReport { get; }
            internal ManualTimestamper Clock { get; } = new(SyncBehindNow);
            internal TimeSpan TickInterval { get; private set; }
            internal InterfaceLogger Logger { get; } = Substitute.For<InterfaceLogger>();
            internal IBlockFinder BlockFinder { get; } = Substitute.For<IBlockFinder>();

            internal SyncBehindHarness(SyncMode syncMode, ulong secondsPerSlot = DefaultSecondsPerSlot, bool isDebug = false)
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

                ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
                pool.InitializedPeersCount.Returns(1);

                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(call =>
                {
                    TickInterval = call.Arg<TimeSpan>();
                    return _timer;
                });

                Logger.IsInfo.Returns(true);
                Logger.IsWarn.Returns(true);
                Logger.IsDebug.Returns(isDebug);
                ILogManager logManager = new OneLoggerLogManager(new ILogger(Logger));

                SyncConfig syncConfig = new();
                _container = new ContainerBuilder()
                    .AddModule(new TestNethermindModule(new ConfigProvider(syncConfig, new BlocksConfig { SecondsPerSlot = secondsPerSlot })))
                    .AddModule(new SynchronizerModule(syncConfig))
                    .AddSingleton(pool)
                    .AddSingleton(BlockFinder)
                    .AddSingleton<ITimestamper>(Clock)
                    .AddSingleton(timerFactory)
                    .AddSingleton(logManager)
                    .AddSingleton(Substitute.For<IWorldStateManager>())
                    .Build();

                SyncReport = (SyncReport)_container.Resolve<ISyncReport>();
                SyncReport.SyncModeSelectorOnChanged(null, new SyncModeChangedEventArgs(SyncMode.None, syncMode));
            }

            /// <summary>Keeps the head <paramref name="secondsBehind"/> behind the clock, however far the clock is advanced.</summary>
            internal void SetHeadBehindBy(ulong secondsBehind) =>
                BlockFinder.Head.Returns(_ => Build.A.Block.WithNumber(1).WithTimestamp(Clock.UnixTime.Seconds - secondsBehind).TestObject);

            internal void Tick() => _timer.Elapsed += Raise.Event();

            /// <summary>Advances the clock to the next sync-behind report and ticks.</summary>
            internal void TickToNextReport()
            {
                Clock.Add(SyncReport.SyncBehindReportInterval);
                Tick();
            }

            internal bool Reported(string message) =>
                Logger.ReceivedCalls().Any(call =>
                    call.GetMethodInfo().Name is nameof(InterfaceLogger.Info) or nameof(InterfaceLogger.Warn)
                    && call.GetArguments() is [string logged] && logged.Contains(message));

            public void Dispose() => _container.Dispose();
        }
    }
}
