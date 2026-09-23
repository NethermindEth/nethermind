// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class BranchProcessorVerdictTests
{
    /// <summary>
    /// A block that fails after its verdict keeps the invalid-block handling unless a request was actually answered
    /// VALID for it: only then does the failure belong to the commit rather than to the block. Sync and every other
    /// caller nobody answered for must still see the block as invalid.
    /// </summary>
    [TestCase(false, typeof(InvalidBlockException), TestName = "Process_FailsAfterUnansweredVerdict_KeepsInvalidBlock")]
    [TestCase(true, typeof(InvalidOperationException), TestName = "Process_FailsAfterAnsweredVerdict_ReportsCommitFailure")]
    public void Process_failure_after_the_verdict_is_a_commit_failure_only_once_answered(bool answered, Type expected)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;

        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns((block, Array.Empty<TxReceipt>()));
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.BeginScope(Arg.Any<BlockHeader>()).Returns(Substitute.For<IDisposable>());

        BranchProcessor branchProcessor = new(blockProcessor, Substitute.For<ISpecProvider>(), worldState, Substitute.For<IBlockhashProvider>(),
            Substitute.For<IInclusionListSatisfactionChecker>(), LimboLogs.Instance);
        branchProcessor.BlockExecuted += (_, e) => e.Answered = answered;
        branchProcessor.BlockProcessed += (_, _) => throw new InvalidBlockException(block, "failed after the verdict");

        Exception thrown = Assert.Catch(() => branchProcessor.Process(Build.A.BlockHeader.TestObject, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance));

        Assert.That(thrown, Is.TypeOf(expected));
    }
}
