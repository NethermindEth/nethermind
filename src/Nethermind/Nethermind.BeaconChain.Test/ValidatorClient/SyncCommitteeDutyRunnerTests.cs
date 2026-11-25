// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.BeaconChain.ValidatorClient;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.ValidatorClient;

// Exact process-wide metric deltas require fixture isolation.
[NonParallelizable]
public class SyncCommitteeDutyRunnerTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ulong Period = (Spec.FuluForkEpoch + 10) / Presets.EpochsPerSyncCommitteePeriod;
    private static readonly ulong NextPeriodStartEpoch = (Period + 1) * Presets.EpochsPerSyncCommitteePeriod;
    /// <summary>The last slot of <see cref="Period"/>.</summary>
    private static readonly ulong LastSlot = NextPeriodStartEpoch * Spec.SlotsPerEpoch - 1;
    private static readonly ulong FailingSlot = (Spec.FuluForkEpoch + 10) * Spec.SlotsPerEpoch + 5;
    private static readonly long SyncMessageDueMs = (long)(Spec.SecondsPerSlot * 1000 * GloasTiming.SyncMessageDueBps / Presets.BasisPoints);
    private static readonly long ContributionDueMs = (long)(Spec.SecondsPerSlot * 1000 * GloasTiming.ContributionDueBps / Presets.BasisPoints);
    private static readonly Hash256 HeadRoot = FilledHash(0x10);
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Signs_a_third_into_each_slot_with_the_committee_of_the_next_slot_and_contributes_two_thirds_in_for_each_selected_subcommittee()
    {
        // Validator 1 holds positions in subcommittees 0 and 1 and validator 2 in subcommittee 0 of this period; validator 3 holds one in subcommittee 2 of the next.
        Harness harness = new(Spec, new() { [Period] = [(1, [0, 130]), (2, [5])], [Period + 1] = [(3, [300])] });
        harness.Signer.Configure().SignSyncCommitteeSelectionProof(Key(1), LastSlot - 1, 1, Arg.Any<CancellationToken>()).Returns(new ValueTask<BlsSignature>(SelectionProof(aggregates: true)));
        harness.Signer.Configure().SignSyncCommitteeSelectionProof(Key(3), LastSlot, 2, Arg.Any<CancellationToken>()).Returns(new ValueTask<BlsSignature>(SelectionProof(aggregates: true)));
        (ulong messagesBefore, ulong contributionsBefore) = (Metrics.BeaconChainValidatorSyncCommitteeMessagesSubmitted, Metrics.BeaconChainValidatorSyncCommitteeContributionsSubmitted);

        await harness.RunFrom(LastSlot - 2, until: LastSlot + 2);

        ulong lastEpoch = NextPeriodStartEpoch - 1;
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.DutiesAsked, Is.EqualTo(new[] { lastEpoch, NextPeriodStartEpoch, NextPeriodStartEpoch }), "the next period is fetched near its start, and the current one again each epoch");
        Assert.That(harness.Subscriptions, Is.EqualTo(new[]
        {
            new[] { (1UL, new ulong[] { 0, 130 }, NextPeriodStartEpoch), (2UL, [5], NextPeriodStartEpoch), (3UL, [300], NextPeriodStartEpoch + Presets.EpochsPerSyncCommitteePeriod) },
            [(3UL, [300], NextPeriodStartEpoch + Presets.EpochsPerSyncCommitteePeriod)],
        }), "each validator on duty is subscribed until the end of its period");
        Assert.That(harness.Submitted, Is.EqualTo(new[]
        {
            new[] { Message(LastSlot - 2, 1), Message(LastSlot - 2, 2) },
            [Message(LastSlot - 1, 1), Message(LastSlot - 1, 2)],
            [Message(LastSlot, 3)],
            [Message(LastSlot + 1, 3)],
        }), "the last slot of a period is signed by the next period's committee");
        Assert.That(harness.HeadRootAskedAtMs, Is.EqualTo(new[] { LastSlot - 2, LastSlot - 1, LastSlot, LastSlot + 1 }.Select(static slot => SlotStartMs(slot) + SyncMessageDueMs)));
        Assert.That(harness.ContributionsAsked, Is.EqualTo(new[] { (LastSlot - 1, 1UL, HeadRoot, SlotStartMs(LastSlot - 1) + ContributionDueMs), (LastSlot, 2UL, HeadRoot, SlotStartMs(LastSlot) + ContributionDueMs) }));
        Assert.That(harness.SubmittedContributions, Is.EqualTo(new[] { new[] { Contribution(LastSlot - 1, 1, 1) }, [Contribution(LastSlot, 3, 2)] }));
        Assert.That(Metrics.BeaconChainValidatorSyncCommitteeMessagesSubmitted - messagesBefore, Is.EqualTo(6UL));
        Assert.That(Metrics.BeaconChainValidatorSyncCommitteeContributionsSubmitted - contributionsBefore, Is.EqualTo(2UL));
    }

    [Test]
    public async Task Duties_of_a_Gloas_slot_or_period_are_neither_fetched_nor_performed()
    {
        Harness harness = new(Spec with { GloasForkEpoch = NextPeriodStartEpoch }, new() { [Period] = [(1, [0])], [Period + 1] = [(1, [0])] });

        await harness.RunFrom(LastSlot - 1, until: LastSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted, Is.EqualTo(new[] { new[] { Message(LastSlot - 1, 1) } }), "the last slot of a period before Gloas is signed by the Gloas period's committee, which is not fetched");
        Assert.That(harness.DutiesAsked, Has.All.LessThan(NextPeriodStartEpoch));
    }

    private static IEnumerable<TestCaseData> FailedMessages()
    {
        yield return Failure("Syncing", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: harness.Clock.CurrentSlot == FailingSlot, optimistic: false)));
        yield return Failure("Optimistic", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: false, optimistic: harness.Clock.CurrentSlot == FailingSlot)));
        yield return Failure("NotReady", static harness => harness.Node.Configure().GetHeadBlockRoot(default).ReturnsForAnyArgs(_ => harness.Clock.CurrentSlot == FailingSlot
            ? throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, "no head yet")
            : new ValueTask<Hash256>(HeadRoot)));
        yield return Failure("Failed", static harness => harness.Node.Configure().SubmitSyncCommitteeMessages(Arg.Is<IReadOnlyList<SyncCommitteeMessage>>(batch => batch[0].Slot == FailingSlot), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("a bug")));
        yield return Failure("SignerRefused", static harness => harness.Signer.Configure().SignSyncCommitteeMessage(Key(1), FailingSlot, Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
            .Throws(new ValidatorSignerException("unknown key", refused: true, innerException: null)));
        yield return Failure("SignerUnreachable", static harness => harness.Signer.Configure().SignSyncCommitteeMessage(Key(1), FailingSlot, Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
            .Throws(new ValidatorSignerException("timed out", refused: false, innerException: null)));
        yield return Failure("RefusedOnSubmit", static harness => harness.Node.Configure().SubmitSyncCommitteeMessages(Arg.Is<IReadOnlyList<SyncCommitteeMessage>>(batch => batch[0].Slot == FailingSlot), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<SubmissionFailure>>([new SubmissionFailure(0, "The signature is invalid.")])));

        static TestCaseData Failure(string reason, Action<Harness> arrange) => new TestCaseData(reason, arrange).SetName($"Sync committee message skipped as {reason}");
    }

    [TestCaseSource(nameof(FailedMessages))]
    public async Task A_failed_message_is_skipped_with_its_reason_and_the_next_slot_still_signs(string reason, Action<Harness> arrange)
    {
        Harness harness = new(Spec, new() { [Period] = [(1, [0])] });
        arrange(harness);
        (long skippedBefore, ulong signerFailuresBefore, ulong submittedBefore) =
            (SkippedMessages(reason), Metrics.BeaconChainValidatorSignerFailures, Metrics.BeaconChainValidatorSyncCommitteeMessagesSubmitted);

        await harness.RunFrom(FailingSlot, until: FailingSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted[^1], Is.EqualTo(new[] { Message(FailingSlot + 1, 1) }));
        Assert.That(SkippedMessages(reason) - skippedBefore, Is.EqualTo(1));
        Assert.That(Metrics.BeaconChainValidatorSignerFailures - signerFailuresBefore, Is.EqualTo(reason.StartsWith("Signer", StringComparison.Ordinal) ? 1UL : 0UL));
        Assert.That(Metrics.BeaconChainValidatorSyncCommitteeMessagesSubmitted - submittedBefore, Is.EqualTo(1UL));
    }

    private static IEnumerable<TestCaseData> FailedContributions()
    {
        yield return Failure("NotFound", static harness => harness.Node.Configure().ProduceSyncCommitteeContribution(FailingSlot, Arg.Any<ulong>(), Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
            .Throws(new BeaconNodeApiException(BeaconNodeApiError.NotFound, "no message held")));
        yield return Failure("Failed", static harness => harness.Node.Configure().ProduceSyncCommitteeContribution(FailingSlot, Arg.Any<ulong>(), Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("a bug")));
        yield return Failure("NoBlockRoot", static harness => harness.Node.Configure().GetHeadBlockRoot(default).ReturnsForAnyArgs(_ => harness.Clock.CurrentSlot == FailingSlot
            ? throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, "no head yet")
            : new ValueTask<Hash256>(HeadRoot)));
        yield return Failure("SignerUnreachable", static harness => harness.Signer.Configure().SignContributionAndProof(Key(1), Arg.Is<ContributionAndProof>(proof => proof.Contribution!.Slot == FailingSlot), Arg.Any<CancellationToken>())
            .Throws(new ValidatorSignerException("timed out", refused: false, innerException: null)));
        yield return Failure("RefusedOnSubmit", static harness => harness.Node.Configure().SubmitContributionAndProofs(
                Arg.Is<IReadOnlyList<SignedContributionAndProof>>(batch => batch[0].Message!.Contribution!.Slot == FailingSlot), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<SubmissionFailure>>([new SubmissionFailure(0, "The aggregate signature is invalid.")])));

        static TestCaseData Failure(string reason, Action<Harness> arrange) => new TestCaseData(reason, arrange).SetName($"Sync committee contribution skipped as {reason}");
    }

    [TestCaseSource(nameof(FailedContributions))]
    public async Task A_failed_contribution_is_skipped_with_its_reason_and_the_next_slot_still_contributes(string reason, Action<Harness> arrange)
    {
        Harness harness = new(Spec, new() { [Period] = [(1, [0])] });
        harness.Signer.Configure().SignSyncCommitteeSelectionProof(default, default, default, default).ReturnsForAnyArgs(new ValueTask<BlsSignature>(SelectionProof(aggregates: true)));
        arrange(harness);
        (long skippedBefore, ulong submittedBefore) = (SkippedContributions(reason), Metrics.BeaconChainValidatorSyncCommitteeContributionsSubmitted);

        await harness.RunFrom(FailingSlot, until: FailingSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.SubmittedContributions[^1], Is.EqualTo(new[] { Contribution(FailingSlot + 1, 1, 0) }));
        Assert.That(SkippedContributions(reason) - skippedBefore, Is.EqualTo(1));
        Assert.That(Metrics.BeaconChainValidatorSyncCommitteeContributionsSubmitted - submittedBefore, Is.EqualTo(1UL));
    }

    [Test]
    public async Task Stops_promptly_when_the_driver_stands_down_or_the_node_shuts_down([Values] bool driverStandsDown)
    {
        Harness harness = new(Spec, new() { [Period] = [(1, [0])] });
        harness.Time.Set(DateTimeOffset.FromUnixTimeMilliseconds(SlotStartMs(FailingSlot)).UtcDateTime);
        using CancellationTokenSource driverStopping = new();
        using SyncCommitteeDutyRunner runner = harness.Runner(static (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));

        Task run = runner.Start(driverStopping.Token);
        if (driverStandsDown) await driverStopping.CancelAsync();
        else await runner.StopAsync();

        Assert.That(async () => await run.WaitAsync(RunBound), Throws.Nothing);
        await harness.Node.DidNotReceiveWithAnyArgs().GetHeadBlockRoot(default);
    }

    private static long SlotStartMs(ulong slot) => (long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot) * 1000;

    /// <summary>The key of the validator at registry index <paramref name="index"/>.</summary>
    private static BlsPublicKey Key(ulong index) => FilledPubkey((byte)(0xa0 + index));

    private static BlsSignature SignatureOf(ulong index) => FilledSignature((byte)(0xb0 + index));

    private static BlsSignature ContributionSignatureOf(ulong index) => FilledSignature((byte)(0xc0 + index));

    /// <summary>What validator <paramref name="index"/> submits for <paramref name="slot"/>.</summary>
    private static (ulong Slot, ulong Validator, Hash256 BlockRoot, BlsSignature Signature) Message(ulong slot, ulong index) => (slot, index, HeadRoot, SignatureOf(index));

    /// <summary>What aggregator <paramref name="index"/> submits for <paramref name="subcommitteeIndex"/> at <paramref name="slot"/>: the node's contribution, signed.</summary>
    private static (ulong Aggregator, ulong Slot, ulong Subcommittee, BlsSignature SelectionProof, BlsSignature Signature) Contribution(ulong slot, ulong index, ulong subcommitteeIndex) =>
        (index, slot, subcommitteeIndex, SelectionProof(aggregates: true), ContributionSignatureOf(index));

    /// <summary>A selection proof that <c>is_sync_committee_aggregator</c> does or does not select.</summary>
    private static BlsSignature SelectionProof(bool aggregates) =>
        Enumerable.Range(0, 256).Select(static fill => FilledSignature((byte)fill)).First(proof => BeaconStateAccessors.IsSyncCommitteeAggregator(proof) == aggregates);

    private static long SkippedMessages(string reason) => Metrics.BeaconChainValidatorSyncCommitteeMessagesSkipped.GetValueOrDefault(new StringLabel(reason));

    private static long SkippedContributions(string reason) => Metrics.BeaconChainValidatorSyncCommitteeContributionsSkipped.GetValueOrDefault(new StringLabel(reason));

    private static ValueTask<SyncingStatus> Status(bool syncing, bool optimistic) => new(new SyncingStatus(0, syncing ? 64UL : 0UL, syncing, optimistic, ExecutionLayerOffline: false));

    /// <summary>A node and signer that serve the sync duties of validators 1 to 3 by period, on a clock that only moves when the runner waits.</summary>
    public sealed class Harness
    {
        private readonly BeaconChainSpec _spec;
        private long _endMs;

        public Harness(BeaconChainSpec spec, Dictionary<ulong, (ulong Validator, ulong[] Positions)[]> dutiesByPeriod)
        {
            _spec = spec;
            Clock = new SlotClock(spec, Time);
            Node.GetSyncingStatus(default).ReturnsForAnyArgs(Status(syncing: false, optimistic: false));
            Node.GetValidators(default!, default).ReturnsForAnyArgs(call => new ValueTask<IReadOnlyList<ValidatorSummary>>(
                [.. call.ArgAt<IReadOnlyList<BlsPublicKey>>(0).Select(static key => new ValidatorSummary(key, key.Bytes[0] - 0xa0UL, 0, Presets.FarFutureEpoch, Slashed: false))]));
            Node.GetSyncDuties(default, default!, default).ReturnsForAnyArgs(call =>
            {
                (ulong epoch, IReadOnlyList<ulong> indices) = (call.ArgAt<ulong>(0), call.ArgAt<IReadOnlyList<ulong>>(1));
                DutiesAsked.Add(epoch);
                SyncDuty[] duties = [.. dutiesByPeriod.GetValueOrDefault(epoch / Presets.EpochsPerSyncCommitteePeriod, []).Where(duty => indices.Contains(duty.Validator))
                    .Select(static duty => new SyncDuty(Key(duty.Validator), duty.Validator, duty.Positions))];
                return new ValueTask<DutiesResult<SyncDuty>>(new DutiesResult<SyncDuty>(null, ExecutionOptimistic: false, duties));
            });
            Node.SubscribeToSyncCommittees(default!, default).ReturnsForAnyArgs(call =>
            {
                Subscriptions.Add([.. call.ArgAt<IReadOnlyList<SyncCommitteeSubscription>>(0).Select(static subscription => (subscription.ValidatorIndex, subscription.SyncCommitteeIndices.ToArray(), subscription.UntilEpoch))]);
                return ValueTask.CompletedTask;
            });
            Node.GetHeadBlockRoot(default).ReturnsForAnyArgs(_ =>
            {
                HeadRootAskedAtMs.Add(Time.UtcNowOffset.ToUnixTimeMilliseconds());
                return new ValueTask<Hash256>(HeadRoot);
            });
            Node.SubmitSyncCommitteeMessages(default!, default).ReturnsForAnyArgs(call =>
            {
                Submitted.Add([.. call.ArgAt<IReadOnlyList<SyncCommitteeMessage>>(0).Select(static message => (message.Slot, message.ValidatorIndex, message.BeaconBlockRoot!, message.Signature))]);
                return new ValueTask<IReadOnlyList<SubmissionFailure>>([]);
            });
            Node.ProduceSyncCommitteeContribution(default, default, default!, default).ReturnsForAnyArgs(call =>
            {
                (ulong slot, ulong subcommitteeIndex, Hash256 blockRoot) = (call.ArgAt<ulong>(0), call.ArgAt<ulong>(1), call.ArgAt<Hash256>(2));
                ContributionsAsked.Add((slot, subcommitteeIndex, blockRoot, Time.UtcNowOffset.ToUnixTimeMilliseconds()));
                return new ValueTask<SyncCommitteeContribution>(new SyncCommitteeContribution
                {
                    Slot = slot,
                    BeaconBlockRoot = blockRoot,
                    SubcommitteeIndex = subcommitteeIndex,
                    AggregationBits = new BitArray(SyncCommitteeContribution.SubcommitteeSize) { [0] = true },
                });
            });
            Node.SubmitContributionAndProofs(default!, default).ReturnsForAnyArgs(call =>
            {
                SubmittedContributions.Add([.. call.ArgAt<IReadOnlyList<SignedContributionAndProof>>(0).Select(static signed =>
                    (signed.Message!.AggregatorIndex, signed.Message.Contribution!.Slot, signed.Message.Contribution.SubcommitteeIndex, signed.Message.SelectionProof, signed.Signature))]);
                return new ValueTask<IReadOnlyList<SubmissionFailure>>([]);
            });
            Signer.SignSyncCommitteeMessage(default, default, default!, default).ReturnsForAnyArgs(call => new ValueTask<BlsSignature>(SignatureOf(call.ArgAt<BlsPublicKey>(0).Bytes[0] - 0xa0UL)));
            Signer.SignSyncCommitteeSelectionProof(default, default, default, default).ReturnsForAnyArgs(new ValueTask<BlsSignature>(SelectionProof(aggregates: false)));
            Signer.SignContributionAndProof(default, default!, default).ReturnsForAnyArgs(call => new ValueTask<BlsSignature>(ContributionSignatureOf(call.ArgAt<ContributionAndProof>(1).AggregatorIndex)));
        }

        public ManualTimestamper Time { get; } = new();
        public SlotClock Clock { get; }
        public IBeaconNodeValidatorApi Node { get; } = Substitute.For<IBeaconNodeValidatorApi>();
        public IValidatorSigner Signer { get; } = Substitute.For<IValidatorSigner>();
        public List<ulong> DutiesAsked { get; } = [];
        public List<(ulong Validator, ulong[] Positions, ulong UntilEpoch)[]> Subscriptions { get; } = [];
        public List<long> HeadRootAskedAtMs { get; } = [];
        public List<(ulong Slot, ulong Validator, Hash256 BlockRoot, BlsSignature Signature)[]> Submitted { get; } = [];
        public List<(ulong Slot, ulong SubcommitteeIndex, Hash256 BlockRoot, long AtMs)> ContributionsAsked { get; } = [];
        public List<(ulong Aggregator, ulong Slot, ulong Subcommittee, BlsSignature SelectionProof, BlsSignature Signature)[]> SubmittedContributions { get; } = [];

        public SyncCommitteeDutyRunner Runner(Func<TimeSpan, CancellationToken, Task> delay) =>
            new(new ValidatorClientConfig { Enabled = true, PublicKeys = [Key(1).ToString(), Key(2).ToString(), Key(3).ToString()] }, Node, Signer, _spec, Clock, LimboLogs.Instance)
            {
                Delay = delay,
            };

        /// <summary>Runs from the start of <paramref name="from"/> until the clock reaches the start of <paramref name="until"/>.</summary>
        public async Task RunFrom(ulong from, ulong until)
        {
            Time.Set(DateTimeOffset.FromUnixTimeMilliseconds(SlotStartMs(from)).UtcDateTime);
            _endMs = SlotStartMs(until);
            using CancellationTokenSource driverStopping = new();
            using SyncCommitteeDutyRunner runner = Runner((delay, cancellationToken) =>
            {
                Time.Add(delay);
                if (Time.UtcNowOffset.ToUnixTimeMilliseconds() >= _endMs) driverStopping.Cancel();
                return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
            });
            await runner.Start(driverStopping.Token).WaitAsync(RunBound);
        }
    }
}
