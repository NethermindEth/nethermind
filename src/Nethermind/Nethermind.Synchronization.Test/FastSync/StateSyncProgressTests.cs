// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            Assert.AreEqual(7, progress.CurrentSyncBlock);
            Assert.AreEqual(0M, progress.LastProgress);
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
            Assert.AreEqual((decimal)expectedResult, progress.LastProgress, "state, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.Empty);
            Assert.AreEqual(0d, progress.LastProgress, "storage, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.Empty);
            Assert.AreEqual(0d, progress.LastProgress, "code, empty");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)expectedResult, progress.LastProgress, "state, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.Saved);
            Assert.AreEqual(0d, progress.LastProgress, "storage, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.Saved);
            Assert.AreEqual(0d, progress.LastProgress, "code, saved");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.State, NodeProgressState.AlreadySaved);
            Assert.AreEqual((decimal)expectedResult, progress.LastProgress, "state, already");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Storage, NodeProgressState.AlreadySaved);
            Assert.AreEqual(0d, progress.LastProgress, "storage, already");

            progress = new BranchProgress(7, LimboTraceLogger.Instance);
            progress.ReportSynced(level, parentIndex, childIndex, NodeDataType.Code, NodeProgressState.AlreadySaved);
            Assert.AreEqual(0d, progress.LastProgress, "code, already");
        }

        [Test]
        public void Multiple_items()
        {
            BranchProgress progress = new(7, LimboTraceLogger.Instance);
            progress.ReportSynced(2, 1, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)1 / 256, progress.LastProgress, "0");

            progress.ReportSynced(2, 1, 2, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)2 / 256, progress.LastProgress, "1");

            progress.ReportSynced(2, 2, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)3 / 256, progress.LastProgress, "2");

            progress.ReportSynced(1, 1, 2, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)18 / 256, progress.LastProgress, "3");

            progress.ReportSynced(2, 3, 1, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)19 / 256, progress.LastProgress, "4");

            progress.ReportSynced(1, 1, 4, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)35 / 256, progress.LastProgress, "5");

            progress.ReportSynced(0, -1, -1, NodeDataType.State, NodeProgressState.Saved);
            Assert.AreEqual((decimal)256 / 256, progress.LastProgress, "6");
        }
    }
}
