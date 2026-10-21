// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.BlockProfiler;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[NonParallelizable]
public class BlockProfilerPluginTests
{
    private static readonly Type CounterType = typeof(CountingBranchProcessor).Assembly.GetType("Nethermind.BlockProfiler.ThreadInstructionCounter")!;
    private static readonly FieldInfo CountersField = CounterType.GetField("t_counters", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ErrorField = CounterType.GetField("s_error", BindingFlags.Static | BindingFlags.NonPublic)!;

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS", null);
        Environment.SetEnvironmentVariable("NETHERMIND_COUNT_INSTRUCTIONS", null);
        CountersField.SetValue(null, null);
        ErrorField.SetValue(null, null);
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

    [Test]
    public void Processing_scope_disposes_counter_handles_on_success_or_failure([Values] bool throwFromProcess)
    {
        IBranchProcessor inner = Substitute.For<IBranchProcessor>();
        inner.Process(Arg.Any<BlockHeader>(), Arg.Any<IReadOnlyList<Block>>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(_ => throwFromProcess ? throw new InvalidOperationException("process failed") : []);
        using CountingBranchProcessor counting = new(inner, LimboLogs.Instance);
        using SafeFileHandle instructions = OpenTestHandle();
        using SafeFileHandle cycles = OpenTestHandle();
        InstallCounters(instructions, cycles);

        bool wasProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = true;
        try
        {
            if (throwFromProcess)
            {
                Assert.Throws<InvalidOperationException>(() => counting.Process(null, [], ProcessingOptions.None, NullBlockTracer.Instance));
            }
            else
            {
                Assert.That(counting.Process(null, [], ProcessingOptions.None, NullBlockTracer.Instance), Is.Empty);
            }
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = wasProcessingThread;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(instructions.IsClosed, Is.True);
            Assert.That(cycles.IsClosed, Is.True);
        }
    }

    [Test]
    public void Nested_processing_scope_keeps_outer_counter_handles_open()
    {
        IBranchProcessor inner = Substitute.For<IBranchProcessor>();
        using CountingBranchProcessor counting = new(inner, LimboLogs.Instance);
        using SafeFileHandle instructions = OpenTestHandle();
        using SafeFileHandle cycles = OpenTestHandle();
        InstallCounters(instructions, cycles);
        bool nested = false;

        inner.Process(Arg.Any<BlockHeader>(), Arg.Any<IReadOnlyList<Block>>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!nested)
                {
                    nested = true;
                    counting.Process(null, [], ProcessingOptions.None, NullBlockTracer.Instance);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(instructions.IsClosed, Is.False);
                        Assert.That(cycles.IsClosed, Is.False);
                    }
                }

                return [];
            });

        bool wasProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = true;
        try
        {
            counting.Process(null, [], ProcessingOptions.None, NullBlockTracer.Instance);
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = wasProcessingThread;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(instructions.IsClosed, Is.True);
            Assert.That(cycles.IsClosed, Is.True);
        }
    }

    private static SafeFileHandle OpenTestHandle() =>
        File.OpenHandle(Path.GetTempFileName(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.DeleteOnClose);

    private static void InstallCounters(SafeHandle instructions, SafeHandle cycles)
    {
        Type countersType = CounterType.GetNestedType("Counters", BindingFlags.NonPublic)!;
        const BindingFlags ConstructorFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ConstructorInfo safeHandleConstructor = countersType.GetConstructor(ConstructorFlags, null, [typeof(SafeHandle), typeof(SafeHandle)], null);
        object counters = safeHandleConstructor is not null
            ? safeHandleConstructor.Invoke([instructions, cycles])!
            : countersType.GetConstructor(ConstructorFlags, null, [typeof(int), typeof(int)], null)!.Invoke([
                unchecked((int)instructions.DangerousGetHandle().ToInt64()),
                unchecked((int)cycles.DangerousGetHandle().ToInt64())])!;
        CountersField.SetValue(null, counters);
    }
}
