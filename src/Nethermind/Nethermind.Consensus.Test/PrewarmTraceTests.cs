// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>BENCH ONLY (bench/prewarm-trace): the trace records a block's transactions and changes nothing it observes.
/// Run once with BENCH_PREWARM_TRACE=1 and once without; each test assumes the setting it checks.</summary>
[TestFixture]
public class PrewarmTraceTests() : PrewarmerHandoffTestBase(Osaka.Instance)
{
    private Block Mixed() => BuildBlock(
        Call(TestItem.PrivateKeyA, 0, Counter),
        Call(TestItem.PrivateKeyB, 0, Counter),
        Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 1.Wei),
        Call(TestItem.PrivateKeyD, 0, Logger),
        Call(TestItem.PrivateKeyB, 1, Reverter));

    [Test]
    public void A_traced_block_records_every_transaction_and_ends_in_the_state_of_executing_it()
    {
        Assume.That(PrewarmTrace.Enabled, Is.True, "needs BENCH_PREWARM_TRACE=1");
        Block block = Mixed();
        Run executed = Process(block, adapter: null);
        Run traced = ProcessWhileWarming(block, static (_, _) => true);

        PrewarmTraceBlock? trace = PrewarmTrace.LastEnded;
        string[] lines = trace?.Format(minMainReads: 0).ToArray() ?? [];
        TestContext.Out.WriteLine(string.Join("\n", lines));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(traced.Results, Is.EqualTo(executed.Results));
            Assert.That(trace, Is.Not.Null);
            Assert.That(trace!.Number, Is.EqualTo(block.Number));
            Assert.That(trace.TxCount, Is.EqualTo(block.Transactions.Length));
            Assert.That(trace.MainStart, Has.All.GreaterThan(0L), "block processing reached every transaction");
            Assert.That(trace.Flags.Count(f => (f & (PrewarmTraceBlock.WarmStarted | PrewarmTraceBlock.SkippedAtStart | PrewarmTraceBlock.JobSkipped)) != 0),
                Is.GreaterThan(0), "the prewarmer touched at least one transaction");
            Assert.That(trace.EndTicks, Is.GreaterThan(trace.StartTicks));
            Assert.That(PrewarmTrace.Current, Is.Null, "the trace ends with the session");
            Assert.That(lines[0], Does.StartWith($"PrewarmTrace block={block.Number} txs={block.Transactions.Length} "));
            Assert.That(lines.Length, Is.EqualTo(1 + block.Transactions.Length), "with minMainReads 0 every transaction gets a line");
        }
    }

    [Test]
    public void Off_it_records_nothing_and_the_block_ends_in_the_state_of_executing_it()
    {
        Assume.That(PrewarmTrace.Enabled, Is.False, "needs BENCH_PREWARM_TRACE unset");
        Block block = Mixed();
        Run executed = Process(block, adapter: null);
        Run traced = ProcessWhileWarming(block, static (_, _) => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traced.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(PrewarmTrace.Current, Is.Null);
            Assert.That(PrewarmTrace.LastEnded, Is.Null);
        }
    }
}
