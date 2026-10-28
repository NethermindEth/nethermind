// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>bogota.md § Deferred inclusion checking: one check per identifier, run once however often it is
/// resent, never restarted by a poll, and evicted rather than completed when it cannot be decided.</summary>
public class InclusionChecksTests
{
    [Test]
    public async Task Identical_checks_share_one_run_and_polling_starts_none()
    {
        TaskCompletionSource<bool?> verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(verdict.Task);
        using InclusionChecks checks = new(evaluator, Substitute.For<IBlockFinder>(), LimboLogs.Instance);
        ExecutionPayload payload = ExecutionPayload.Create(Build.A.Block.WithNumber(1).TestObject);

        bool unknownBeforeStart = checks.TryGetVerdict(TestItem.KeccakA, out _);
        bool? first = checks.Start(TestItem.KeccakA, payload, Lists(), []);
        bool? second = checks.Start(TestItem.KeccakA, payload, Lists(), []);
        bool pendingKnown = checks.TryGetVerdict(TestItem.KeccakA, out bool? pending);
        verdict.SetResult(false);
        bool? completed = await PollAsync(checks, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unknownBeforeStart, Is.False);
            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            Assert.That(pendingKnown, Is.True);
            Assert.That(pending, Is.Null);
            Assert.That(completed, Is.False);
            Assert.That(checks.Start(TestItem.KeccakA, payload, Lists(), []), Is.False, "a completed verdict answers a resend at once");
            await evaluator.Received(1).EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>());
            Assert.That(checks.TryGetVerdict(TestItem.KeccakB, out _), Is.False, "polling an unknown identifier starts no check");
        }
    }

    /// <summary>Missing local data never yields a verdict: the check is evicted, and resubmitting it runs it again.</summary>
    [Test]
    public async Task Undecidable_check_is_evicted_and_rerun_on_resubmission()
    {
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<bool?>(null), Task.FromResult<bool?>(true));
        using InclusionChecks checks = new(evaluator, Substitute.For<IBlockFinder>(), LimboLogs.Instance);
        ExecutionPayload payload = ExecutionPayload.Create(Build.A.Block.WithNumber(1).TestObject);

        checks.Start(TestItem.KeccakA, payload, Lists(), []);
        bool? undecided = await PollAsync(checks, TestItem.KeccakA);
        bool knownAfterUndecided = checks.TryGetVerdict(TestItem.KeccakA, out _);
        checks.Start(TestItem.KeccakA, payload, Lists(), []);
        bool? rerun = await PollAsync(checks, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(undecided, Is.Null);
            Assert.That(knownAfterUndecided, Is.False);
            Assert.That(rerun, Is.True);
            await evaluator.Received(2).EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>());
        }
    }

    /// <summary>bogota.md § Deferred inclusion checking: past capacity a check for a finalized or pruned block goes
    /// first, then a pending one, and a completed verdict for an unfinalized block only when nothing else is left.</summary>
    [TestCase(null, 2UL, "pending", TestName = "Pending check is evicted before an unfinalized verdict")]
    [TestCase(1UL, 2UL, "finalized", TestName = "Verdict at or below the finalized block is evicted before a pending check")]
    [TestCase(0UL, 0UL, "pending", TestName = "Check added at or below the finalized block does not evict itself")]
    public async Task Eviction_keeps_unfinalized_verdicts(ulong? finalizedNumber, ulong addedNumber, string expectedEvicted)
    {
        TaskCompletionSource<bool?> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Is<Block>(b => b.Number != 3), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        evaluator.EvaluateAsync(Arg.Is<Block>(b => b.Number == 3), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(never.Task);
        using InclusionChecks checks = new(evaluator, FinalizedAt(finalizedNumber), LimboLogs.Instance);

        Hash256 finalized = Keccak.Compute("finalized");
        checks.Start(finalized, Payload(1), Lists(), []);
        await PollAsync(checks, finalized);
        Hash256 pending = Keccak.Compute("pending");
        checks.Start(pending, Payload(3), Lists(), []);
        for (int i = 2; i < InclusionChecks.Capacity; i++)
        {
            Hash256 id = Keccak.Compute(i.ToString());
            checks.Start(id, Payload(2), Lists(), []);
            await PollAsync(checks, id);
        }
        checks.Start(TestItem.KeccakA, Payload(addedNumber), Lists(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checks.TryGetVerdict(finalized, out _), Is.EqualTo(expectedEvicted != "finalized"));
            Assert.That(checks.TryGetVerdict(pending, out _), Is.EqualTo(expectedEvicted != "pending"));
            Assert.That(checks.TryGetVerdict(TestItem.KeccakA, out _), Is.True, "the check being added is never the one evicted");
        }
    }

    /// <summary>Checks share a bounded number of runs, and an evicted pending check gives its run back.</summary>
    [Test]
    public async Task Concurrent_runs_are_bounded_and_eviction_cancels_a_pending_check()
    {
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith<bool?>(_ => null));
        using InclusionChecks checks = new(evaluator, Substitute.For<IBlockFinder>(), LimboLogs.Instance);

        int Runs() => evaluator.ReceivedCalls().Count();
        for (int i = 0; i < InclusionChecks.MaxConcurrentChecks + 1; i++) checks.Start(Keccak.Compute(i.ToString()), Payload(1), Lists(), []);
        await WaitUntilAsync(() => Runs() == InclusionChecks.MaxConcurrentChecks);
        bool oldestRuns = checks.TryGetVerdict(Keccak.Compute("0"), out _);

        // Fills to capacity and one past it, which evicts the oldest pending check, a running one.
        for (int i = InclusionChecks.MaxConcurrentChecks + 1; i <= InclusionChecks.Capacity; i++) checks.Start(Keccak.Compute(i.ToString()), Payload(1), Lists(), []);
        await WaitUntilAsync(() => Runs() == InclusionChecks.MaxConcurrentChecks + 1);
        await Task.Delay(50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldestRuns, Is.True);
            Assert.That(checks.TryGetVerdict(Keccak.Compute("0"), out _), Is.False);
            Assert.That(Runs(), Is.EqualTo(InclusionChecks.MaxConcurrentChecks + 1), "the cancelled run lets exactly one waiting check in");
        }
    }

    [Test]
    public void Oldest_check_is_evicted_past_capacity()
    {
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        using InclusionChecks checks = new(evaluator, Substitute.For<IBlockFinder>(), LimboLogs.Instance);
        ExecutionPayload payload = ExecutionPayload.Create(Build.A.Block.WithNumber(1).TestObject);

        for (int i = 0; i <= InclusionChecks.Capacity; i++) checks.Start(Keccak.Compute(i.ToString()), payload, Lists(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checks.TryGetVerdict(Keccak.Compute("0"), out _), Is.False);
            Assert.That(checks.TryGetVerdict(Keccak.Compute("1"), out _), Is.True);
            Assert.That(checks.TryGetVerdict(Keccak.Compute(InclusionChecks.Capacity.ToString()), out _), Is.True);
        }
    }

    /// <returns>The completed verdict, or <c>null</c> once the check is unknown.</returns>
    private static async Task<bool?> PollAsync(InclusionChecks checks, Hash256 id)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (checks.TryGetVerdict(id, out bool? verdict))
        {
            if (verdict is not null) return verdict;
            await Task.Delay(5, timeout.Token);
        }
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static ExecutionPayload Payload(ulong number) => ExecutionPayload.Create(Build.A.Block.WithNumber(number).TestObject);

    private static IBlockFinder FinalizedAt(ulong? number)
    {
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        if (number is { } finalized)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(finalized).TestObject;
            finder.FinalizedHash.Returns(header.Hash);
            finder.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
        }
        return finder;
    }

    private static byte[][][] Lists() => [.. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize)];
}
