// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// resent, never restarted by a poll, and forgotten rather than completed when it cannot be decided.</summary>
public class InclusionChecksTests
{
    [Test]
    public async Task Identical_checks_share_one_run_and_polling_starts_none()
    {
        TaskCompletionSource<bool?> verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(verdict.Task);
        using InclusionChecks checks = new(evaluator, LimboLogs.Instance);
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

    [Test]
    public async Task Undecidable_check_is_forgotten()
    {
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(null));
        using InclusionChecks checks = new(evaluator, LimboLogs.Instance);

        checks.Start(TestItem.KeccakA, ExecutionPayload.Create(Build.A.Block.WithNumber(1).TestObject), Lists(), []);

        Assert.That(await PollAsync(checks, TestItem.KeccakA), Is.Null);
    }

    [Test]
    public void Oldest_check_is_evicted_past_capacity()
    {
        IInclusionListComplianceEvaluator evaluator = Substitute.For<IInclusionListComplianceEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Block>(), Arg.Any<Transaction[][]>(), Arg.Any<OmissionClaim[]>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        using InclusionChecks checks = new(evaluator, LimboLogs.Instance);
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

    private static byte[][][] Lists() => [.. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize)];
}
