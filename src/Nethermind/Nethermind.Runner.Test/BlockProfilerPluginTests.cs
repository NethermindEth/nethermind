// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BlockProfiler;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[NonParallelizable]
public class BlockProfilerPluginTests
{
    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS", null);
        Environment.SetEnvironmentVariable("NETHERMIND_COUNT_INSTRUCTIONS", null);
    }

    [TestCase("", false)]
    [TestCase("not-a-number", false)]
    [TestCase("123", true)]
    [TestCase(" 123 , 456 ", true)]
    public void Enabled_follows_the_profile_blocks_variable(string value, bool expected)
    {
        Environment.SetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS", value);
        Assert.That(new BlockProfilerPlugin().Enabled, Is.EqualTo(expected));
    }

    [TestCase("1", true)]
    [TestCase("0", false)]
    [TestCase("", false)]
    public void Enabled_follows_the_count_instructions_variable(string value, bool expected)
    {
        Environment.SetEnvironmentVariable("NETHERMIND_COUNT_INSTRUCTIONS", value);
        Assert.That(new BlockProfilerPlugin().Enabled, Is.EqualTo(expected));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Counting_holds_the_verdicts_until_the_branch_completes(bool branchSucceeds)
    {
        IBranchProcessor inner = Substitute.For<IBranchProcessor>();
        using CountingBranchProcessor counting = new(inner, LimboLogs.Instance);
        List<Block> answered = [];
        counting.BlockExecuted += (_, e) => answered.Add(e.Block);
        Block first = Build.A.Block.WithNumber(1).TestObject;
        Block second = Build.A.Block.WithNumber(2).TestObject;

        bool wasProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = true;
        try
        {
            inner.BlocksProcessing += Raise.EventWith(inner, new BlocksProcessingEventArgs([first, second]));
            inner.BlockExecuted += Raise.EventWith(inner, new BlockExecutedEventArgs(first));
            inner.BlockExecuted += Raise.EventWith(inner, new BlockExecutedEventArgs(second));
            Assert.That(answered, Is.Empty, "no verdict leaves before the branch completes");

            inner.BranchProcessingCompleted += Raise.EventWith(inner, branchSucceeds
                ? new BranchProcessingCompletedEventArgs([first, second], 2)
                : new BranchProcessingCompletedEventArgs([first, second], 1, new InvalidOperationException()));
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = wasProcessingThread;
        }

        Assert.That(answered, Is.EqualTo(branchSucceeds ? new[] { first, second } : Array.Empty<Block>()));
    }

    [Test]
    public void Counting_passes_the_verdicts_of_other_threads_branches_straight_through()
    {
        // Block building and the tracing calls run branches of their own, off the block processing thread.
        IBranchProcessor inner = Substitute.For<IBranchProcessor>();
        using CountingBranchProcessor counting = new(inner, LimboLogs.Instance);
        List<Block> answered = [];
        counting.BlockExecuted += (_, e) => answered.Add(e.Block);
        Block block = Build.A.Block.WithNumber(1).TestObject;

        Assert.That(ProcessingThread.IsBlockProcessingThread, Is.False);
        inner.BlocksProcessing += Raise.EventWith(inner, new BlocksProcessingEventArgs([block]));
        inner.BlockExecuted += Raise.EventWith(inner, new BlockExecutedEventArgs(block));

        Assert.That(answered, Is.EqualTo(new[] { block }));
    }
}
