// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using BlockchainMetrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Test;

[NonParallelizable]
public class ProcessingStatsTests
{
    [Test]
    public async Task UpdateStats_aggregates_block_totals_for_processing_window() =>
        await WithRestoredBlockchainMetrics(async () =>
        {
            ProcessingStats processingStats = CreateProcessingStats(out TaskCompletionSource<BlockStatistics> completion);

            (BlockHeader baseBlock, Block block1, Block block2) = BuildBlockTrio();

            processingStats.UpdateStats([block1, block2], baseBlock, 500_000);

            BlockStatistics stats = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStats(stats, blockCount: 2, blockFrom: 1, blockTo: 2, processingMs: 500, mGasPerSecond: 10);
        });

    [Test]
    public async Task UpdateStats_aggregates_multiple_updates_until_report_window() =>
        await WithRestoredBlockchainMetrics(async () =>
        {
#if DEBUG
            Assert.Ignore("ProcessingStats emits every report when debug logging is enabled.");
#endif
            TestProcessingStats processingStats = CreateTestProcessingStats(out TaskCompletionSource<BlockStatistics> completion);
            processingStats.Start();

            (BlockHeader baseBlock, Block block1, Block block2) = BuildBlockTrio();

            processingStats.GenerateReportForTest(block1, baseBlock, blockCount: 1, gasUsed: 2_000_000, transactionCount: 2, processingMicroseconds: 200_000);
            processingStats.AdvanceReportWindow();
            processingStats.GenerateReportForTest(block2, block1.Header, blockCount: 1, gasUsed: 3_000_000, transactionCount: 1, processingMicroseconds: 300_000);

            BlockStatistics stats = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStats(stats, blockCount: 2, blockFrom: 1, blockTo: 2, processingMs: 500, mGasPerSecond: 10);
        });

    private static void AssertStats(BlockStatistics stats, long blockCount, ulong blockFrom, ulong blockTo, double processingMs, double mGasPerSecond)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.BlockCount, Is.EqualTo(blockCount));
            Assert.That(stats.BlockFrom, Is.EqualTo(blockFrom));
            Assert.That(stats.BlockTo, Is.EqualTo(blockTo));
            Assert.That(stats.ProcessingMs, Is.EqualTo(processingMs));
            Assert.That(stats.MGasPerSecond, Is.EqualTo(mGasPerSecond));
        }
    }

    private static (BlockHeader BaseBlock, Block Block1, Block Block2) BuildBlockTrio()
    {
        BlockHeader baseBlock = Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(TestItem.KeccakA)
            .TestObject;
        Block block1 = Build.A.Block
            .WithParent(baseBlock)
            .WithGasUsed(2_000_000)
            .WithStateRoot(TestItem.KeccakB)
            .WithTransactions(new Transaction(), new Transaction())
            .TestObject;
        Block block2 = Build.A.Block
            .WithParent(block1)
            .WithGasUsed(3_000_000)
            .WithStateRoot(TestItem.KeccakC)
            .WithTransactions(new Transaction())
            .TestObject;
        return (baseBlock, block1, block2);
    }

    private static ProcessingStats CreateProcessingStats(out TaskCompletionSource<BlockStatistics> completion)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        ProcessingStats processingStats = new(stateReader, new TestLogManager(LogLevel.Warn));
        TaskCompletionSource<BlockStatistics> statsCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        processingStats.NewProcessingStatistics += (_, stats) => statsCompletion.TrySetResult(stats);
        completion = statsCompletion;
        return processingStats;
    }

    private static TestProcessingStats CreateTestProcessingStats(out TaskCompletionSource<BlockStatistics> completion)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        TestProcessingStats processingStats = new(stateReader);
        TaskCompletionSource<BlockStatistics> statsCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        processingStats.NewProcessingStatistics += (_, stats) => statsCompletion.TrySetResult(stats);
        completion = statsCompletion;
        return processingStats;
    }

    private static async Task WithRestoredBlockchainMetrics(Func<Task> test)
    {
        double originalMgas = BlockchainMetrics.Mgas;
        double originalMgasPerSec = BlockchainMetrics.MgasPerSec;
        long originalTransactions = BlockchainMetrics.Transactions;
        ulong originalBlocks = BlockchainMetrics.Blocks;
        ulong originalBlockchainHeight = BlockchainMetrics.BlockchainHeight;
        UInt256 originalTotalDifficulty = BlockchainMetrics.TotalDifficulty;
        UInt256 originalLastDifficulty = BlockchainMetrics.LastDifficulty;
        ulong originalGasUsed = BlockchainMetrics.GasUsed;
        ulong originalGasLimit = BlockchainMetrics.GasLimit;

        try
        {
            await test();
        }
        finally
        {
            BlockchainMetrics.Mgas = originalMgas;
            BlockchainMetrics.MgasPerSec = originalMgasPerSec;
            BlockchainMetrics.Transactions = originalTransactions;
            BlockchainMetrics.Blocks = originalBlocks;
            BlockchainMetrics.BlockchainHeight = originalBlockchainHeight;
            BlockchainMetrics.TotalDifficulty = originalTotalDifficulty;
            BlockchainMetrics.LastDifficulty = originalLastDifficulty;
            BlockchainMetrics.GasUsed = originalGasUsed;
            BlockchainMetrics.GasLimit = originalGasLimit;
        }
    }

    private sealed class TestProcessingStats(IStateReader stateReader)
        : ProcessingStats(stateReader, new TestLogManager(LogLevel.Warn))
    {
        private long _reportMs;

        public void AdvanceReportWindow() => _reportMs += 1_001;

        public void GenerateReportForTest(
            Block block,
            BlockHeader baseBlock,
            long blockCount,
            ulong gasUsed,
            long transactionCount,
            long processingMicroseconds) =>
            GenerateReport(new BlockData
            {
                Block = block,
                BaseBlock = baseBlock,
                BlockCount = blockCount,
                FirstBlockNumber = block.Number,
                GasUsed = gasUsed,
                TransactionCount = transactionCount,
                ProcessingMicroseconds = processingMicroseconds
            });

        protected override long GetReportMs() => _reportMs;
    }
}
