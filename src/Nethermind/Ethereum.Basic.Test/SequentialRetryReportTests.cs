// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Ethereum.Test.Base;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class SequentialRetryReportTests
{
    [Test]
    public void A_parallel_failure_retried_sequentially_is_reported_once_as_a_retry([Values] bool sequentialAccepts)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        RecordingReport report = new();
        SequentialRetryReport retryReport = new(report);
        IBlockProcessor processor = retryReport.Decorate(new NodeLikeBlockProcessor(retryReport, sequentialAccepts));

        bool accepted = ProcessWithRetry(processor, block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(sequentialAccepts));
            Assert.That(report.Lines, Is.EqualTo(new[]
            {
                "balExecution 1 parallel",
                $"balFallback 1 parallel failed {(sequentialAccepts ? "valid" : "sequential failed")}",
            }));
        }
    }

    [Test]
    public void A_block_forced_sequential_from_the_start_is_a_decision_not_a_retry()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        RecordingReport report = new();
        SequentialRetryReport retryReport = new(report);
        IBlockProcessor processor = retryReport.Decorate(new NodeLikeBlockProcessor(retryReport, sequentialAccepts: true));

        processor.ProcessOne(block, ProcessingOptions.ForceSequentialBlockAccessList, NullBlockTracer.Instance, Amsterdam.Instance);

        Assert.That(report.Lines, Is.EqualTo(new[] { "balExecution 1 forced" }));
    }

    /// <summary>Retries the way <see cref="BranchProcessor"/> does after the parallel executor rejects a block.</summary>
    private static bool ProcessWithRetry(IBlockProcessor processor, Block block)
    {
        try
        {
            processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        }
        catch (Exception)
        {
            try
            {
                processor.ProcessOne(block, ProcessingOptions.ForceSequentialBlockAccessList, NullBlockTracer.Instance, Amsterdam.Instance);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports each decision as the node's access-list manager does, fails every parallel attempt with a wrapped
    /// error as the node's processor does, and accepts or rejects the sequential run.
    /// </summary>
    private sealed class NodeLikeBlockProcessor(SequentialRetryReport observer, bool sequentialAccepts) : IBlockProcessor
    {
        public event Action<Block> TransactionsExecuted { add { } remove { } }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            bool parallel = !options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList);
            observer.OnExecutionPathChosen(suggestedBlock, parallel ? null : "forced");
            if (parallel) throw new InvalidOperationException("retry", new InvalidOperationException("parallel failed"));
            if (!sequentialAccepts) throw new InvalidOperationException("sequential failed");
            return (suggestedBlock, []);
        }
    }

    private sealed class RecordingReport : IBlockAccessListExecutionReport
    {
        public List<string> Lines { get; } = [];

        public void OnExecutionPathChosen(Block block, string sequentialReason) =>
            Lines.Add($"balExecution {block.Number} {sequentialReason ?? "parallel"}");

        public void OnSequentialRetry(Block block, Exception parallelError, Exception sequentialError) =>
            Lines.Add($"balFallback {block.Number} {parallelError.Message} {sequentialError?.Message ?? "valid"}");
    }
}
