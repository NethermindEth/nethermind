// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class StateSyncProgressTests
    {
        [Test]
        public void Start_values_are_correct()
        {
            BranchProgress progress = new(7, LimboTraceLogger.Instance);
            Assert.That(progress.CurrentSyncBlock, Is.EqualTo(7));
            Assert.That(progress.LastProgress, Is.EqualTo(0M));
        }

        [TestCase(0, -1, -1, 1d)]
        [TestCase(1, -1, 1, (double)1 / 16)]
        [TestCase(1, 1, -1, (double)1)]
        [TestCase(2, 1, 1, (double)1 / 256)]
        [TestCase(2, 1, -1, (double)1 / 16)]
        [TestCase(2, -1, 1, (double)1 / 16)]
        public void Single_item_progress_is_correct(int level, int parentIndex, int childIndex, double expectedResult)
        {
            BranchProgress progress = new(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.State, NodeProgressState.Empty);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)expectedResult), "state, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.Empty);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "storage, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.Empty);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "code, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)expectedResult), "state, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "storage, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "code, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.State, NodeProgressState.AlreadySaved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)expectedResult), "state, already");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.AlreadySaved);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "storage, already");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.AlreadySaved);
            Assert.That(progress.LastProgress, Is.EqualTo(0d), "code, already");
        }

        [Test]
        public void Multiple_items()
        {
            BranchProgress progress = new(7, LimboTraceLogger.Instance);
            progress.ReportSynced(2, 1, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)1 / 256), "0");

            progress.ReportSynced(2, 1, 2, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)2 / 256), "1");

            progress.ReportSynced(2, 2, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)3 / 256), "2");

            progress.ReportSynced(1, 1, 2, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)18 / 256), "3");

            progress.ReportSynced(2, 3, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)19 / 256), "4");

            progress.ReportSynced(1, 1, 4, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)35 / 256), "5");

            progress.ReportSynced(0, -1, -1, NodeDataType.State, NodeProgressState.Saved);
            Assert.That(progress.LastProgress, Is.EqualTo((decimal)256 / 256), "6");
        }
    }
}
