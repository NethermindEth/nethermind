// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.BeaconChain.ValidatorClient;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.ValidatorClient;

// Exact process-wide metric deltas require fixture isolation.
[NonParallelizable]
public class ProposerDutyRunnerTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ulong Epoch = Spec.FuluForkEpoch + 10;
    private static readonly ulong ProposalSlot = Epoch * Spec.SlotsPerEpoch + 5;
    private static readonly Address FeeRecipient = new(Bytes.FromHexString("0x00000000000000000000000000000000000000fe"));
    private static readonly byte[][] Blobs = [Bytes.FromHexString("0x01"), Bytes.FromHexString("0x02")];
    private static readonly byte[][] Proofs = [Bytes.FromHexString("0x03")];
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Prepares_the_fee_recipient_once_an_epoch_and_at_the_start_of_each_slot_of_its_own_signs_and_publishes_the_nodes_block()
    {
        // Validator 9 is not configured, so the block of its slot is not asked for.
        Harness harness = new(Spec, new() { [ProposalSlot] = 1, [ProposalSlot + 1] = 9, [ProposalSlot + 2] = 2, [Epoch * Spec.SlotsPerEpoch + Spec.SlotsPerEpoch] = 1 });
        ulong proposedBefore = Metrics.BeaconChainValidatorBlocksProposed;

        await harness.RunFrom(ProposalSlot, until: (Epoch + 1) * Spec.SlotsPerEpoch + 1);

        ulong nextEpochSlot = (Epoch + 1) * Spec.SlotsPerEpoch;
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Prepared, Is.EqualTo(new[] { new[] { (1UL, FeeRecipient), (2UL, FeeRecipient) }, [(1UL, FeeRecipient), (2UL, FeeRecipient)] }), "every configured validator found, once each epoch");
        Assert.That(harness.Produced, Is.EqualTo(new[]
        {
            (ProposalSlot, RandaoRevealOf(1, Epoch), Hash256.Zero, SlotStartMs(ProposalSlot)),
            (ProposalSlot + 2, RandaoRevealOf(2, Epoch), Hash256.Zero, SlotStartMs(ProposalSlot + 2)),
            (nextEpochSlot, RandaoRevealOf(1, Epoch + 1), Hash256.Zero, SlotStartMs(nextEpochSlot)),
        }));
        Assert.That(harness.Published.Select(static contents => (contents.SignedBlock.Message!.Slot, contents.SignedBlock.Signature, contents.Blobs, contents.KzgProofs)), Is.EqualTo(new[]
        {
            (ProposalSlot, BlockSignatureOf(1), Blobs, Proofs),
            (ProposalSlot + 2, BlockSignatureOf(2), Blobs, Proofs),
            (nextEpochSlot, BlockSignatureOf(1), Blobs, Proofs),
        }));
        Assert.That(harness.Published.Select(static contents => SszRoots.HashTreeRoot(contents.SignedBlock.Message!)), Is.EqualTo(harness.SignedHeaders.Select(static header => SszRoots.HashTreeRoot(header))),
            "the header signed has the root of the block published");
        Assert.That(Metrics.BeaconChainValidatorBlocksProposed - proposedBefore, Is.EqualTo(3UL));
    }

    private static IEnumerable<TestCaseData> FailedProposals()
    {
        yield return Failure("Syncing", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: harness.Clock.CurrentSlot == ProposalSlot, optimistic: false)));
        yield return Failure("Optimistic", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: false, optimistic: harness.Clock.CurrentSlot == ProposalSlot)));
        yield return Failure("NotReady", static harness => harness.Node.Configure().ProduceBlock(ProposalSlot, Arg.Any<BlsSignature>(), Arg.Any<Hash256>(), Arg.Any<CancellationToken>())
            .Throws(new BeaconNodeApiException(BeaconNodeApiError.NotReady, "the execution layer built no payload")));
        yield return Failure("UnexpectedBlock", static harness => harness.OtherFeeRecipientAt = ProposalSlot);
        yield return Failure("SignerUnreachable", static harness => harness.Signer.Configure().SignRandaoReveal(Key(1), Epoch, Arg.Any<CancellationToken>())
            .Throws(new ValidatorSignerException("timed out", refused: false, innerException: null)));
        yield return Failure("SignerRefused", static harness => harness.Signer.Configure().SignBlock(Key(1), Arg.Any<BeaconBlockHeader>(), Arg.Any<CancellationToken>())
            .Throws(new ValidatorSignerException("slashable", refused: true, innerException: null)));
        yield return Failure("InvalidRequest", static harness => harness.Node.Configure().PublishBlock(Arg.Is<SignedBlockContents>(contents => contents.SignedBlock.Message!.Slot == ProposalSlot), Arg.Any<CancellationToken>())
            .Throws(new BeaconNodeApiException(BeaconNodeApiError.InvalidRequest, "The proposer signature is invalid.")));
        yield return Failure("Failed", static harness => harness.Node.Configure().PublishBlock(Arg.Is<SignedBlockContents>(contents => contents.SignedBlock.Message!.Slot == ProposalSlot), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("a bug")));

        static TestCaseData Failure(string reason, Action<Harness> arrange) => new TestCaseData(reason, arrange).SetName($"Block proposal skipped as {reason}");
    }

    [TestCaseSource(nameof(FailedProposals))]
    public async Task A_failed_proposal_is_skipped_with_its_reason_and_the_next_one_still_publishes(string reason, Action<Harness> arrange)
    {
        Harness harness = new(Spec, new() { [ProposalSlot] = 1, [ProposalSlot + 1] = 2 });
        arrange(harness);
        (long skippedBefore, ulong signerFailuresBefore, ulong proposedBefore) = (Skipped(reason), Metrics.BeaconChainValidatorSignerFailures, Metrics.BeaconChainValidatorBlocksProposed);

        await harness.RunFrom(ProposalSlot, until: ProposalSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Published.Select(static contents => contents.SignedBlock.Message!.Slot).Last(), Is.EqualTo(ProposalSlot + 1));
        Assert.That(Skipped(reason) - skippedBefore, Is.EqualTo(1));
        Assert.That(Metrics.BeaconChainValidatorSignerFailures - signerFailuresBefore, Is.EqualTo(reason.StartsWith("Signer", StringComparison.Ordinal) ? 1UL : 0UL));
        Assert.That(Metrics.BeaconChainValidatorBlocksProposed - proposedBefore, Is.EqualTo(1UL));
    }

    [Test]
    public async Task Stops_promptly_when_the_driver_stands_down_or_the_node_shuts_down([Values] bool driverStandsDown)
    {
        Harness harness = new(Spec, new() { [ProposalSlot + 1] = 1 });
        harness.Time.Set(DateTimeOffset.FromUnixTimeMilliseconds(SlotStartMs(ProposalSlot)).UtcDateTime);
        using CancellationTokenSource driverStopping = new();
        using ProposerDutyRunner runner = harness.Runner(static (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));

        Task run = runner.Start(driverStopping.Token);
        if (driverStandsDown) await driverStopping.CancelAsync();
        else await runner.StopAsync();

        Assert.That(async () => await run.WaitAsync(RunBound), Throws.Nothing);
        await harness.Node.DidNotReceiveWithAnyArgs().ProduceBlock(default, default, default!, default);
    }

    private static long SlotStartMs(ulong slot) => (long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot) * 1000;

    /// <summary>The key of the validator at registry index <paramref name="index"/>.</summary>
    private static BlsPublicKey Key(ulong index) => FilledPubkey((byte)(0xa0 + index));

    private static BlsSignature RandaoRevealOf(ulong index, ulong epoch) => FilledSignature((byte)(0x40 + index * 4 + epoch % 4));

    private static BlsSignature BlockSignatureOf(ulong index) => FilledSignature((byte)(0xb0 + index));

    private static long Skipped(string reason) => Metrics.BeaconChainValidatorProposalsSkipped.GetValueOrDefault(new StringLabel(reason));

    private static ValueTask<SyncingStatus> Status(bool syncing, bool optimistic) => new(new SyncingStatus(0, syncing ? 64UL : 0UL, syncing, optimistic, ExecutionLayerOffline: false));

    /// <summary>A node and signer that serve the proposers of each slot, validators 1 and 2 configured, on a clock that only moves when the runner waits.</summary>
    public sealed class Harness
    {
        private readonly BeaconChainSpec _spec;
        private long _endMs;

        /// <param name="proposers">The proposer of each slot; any other slot is proposed by validator 50.</param>
        public Harness(BeaconChainSpec spec, Dictionary<ulong, ulong> proposers)
        {
            _spec = spec;
            Clock = new SlotClock(spec, Time);
            Node.GetSyncingStatus(default).ReturnsForAnyArgs(Status(syncing: false, optimistic: false));
            Node.GetValidators(default!, default).ReturnsForAnyArgs(call => new ValueTask<IReadOnlyList<ValidatorSummary>>(
                [.. call.ArgAt<IReadOnlyList<BlsPublicKey>>(0).Select(static key => new ValidatorSummary(key, key.Bytes[0] - 0xa0UL, 0, Presets.FarFutureEpoch, Slashed: false))]));
            Node.GetProposerDuties(default, default).ReturnsForAnyArgs(call =>
            {
                ulong startSlot = call.ArgAt<ulong>(0) * spec.SlotsPerEpoch;
                ProposerDuty[] duties = [.. Enumerable.Range(0, (int)spec.SlotsPerEpoch).Select(offset => startSlot + (ulong)offset)
                    .Select(slot => new ProposerDuty(Key(proposers.GetValueOrDefault(slot, 50UL)), proposers.GetValueOrDefault(slot, 50UL), slot))];
                return new ValueTask<DutiesResult<ProposerDuty>>(new DutiesResult<ProposerDuty>(Hash256.Zero, ExecutionOptimistic: false, duties));
            });
            Node.PrepareBeaconProposers(default!, default).ReturnsForAnyArgs(call =>
            {
                Prepared.Add([.. call.ArgAt<IReadOnlyList<ProposerPreparation>>(0).Select(static preparation => (preparation.ValidatorIndex, preparation.FeeRecipient))]);
                return ValueTask.CompletedTask;
            });
            Node.ProduceBlock(default, default, default!, default).ReturnsForAnyArgs(call =>
            {
                (ulong slot, BlsSignature randaoReveal, Hash256 graffiti) = (call.ArgAt<ulong>(0), call.ArgAt<BlsSignature>(1), call.ArgAt<Hash256>(2));
                Produced.Add((slot, randaoReveal, graffiti, Time.UtcNowOffset.ToUnixTimeMilliseconds()));
                BeaconBlock block = MinimalBlock(slot).Message!;
                block.ProposerIndex = proposers[slot];
                block.Body!.RandaoReveal = randaoReveal;
                block.Body.ExecutionPayload!.FeeRecipient = OtherFeeRecipientAt == slot ? Address.Zero : FeeRecipient;
                return new ValueTask<ProducedBlock>(new ProducedBlock(block, Blobs, Proofs, 0, 0));
            });
            Node.PublishBlock(default!, default).ReturnsForAnyArgs(call =>
            {
                Published.Add(call.ArgAt<SignedBlockContents>(0));
                return ValueTask.CompletedTask;
            });
            Signer.SignRandaoReveal(default, default, default).ReturnsForAnyArgs(call => new ValueTask<BlsSignature>(RandaoRevealOf(call.ArgAt<BlsPublicKey>(0).Bytes[0] - 0xa0UL, call.ArgAt<ulong>(1))));
            Signer.SignBlock(default, default!, default).ReturnsForAnyArgs(call =>
            {
                SignedHeaders.Add(call.ArgAt<BeaconBlockHeader>(1));
                return new ValueTask<BlsSignature>(BlockSignatureOf(call.ArgAt<BlsPublicKey>(0).Bytes[0] - 0xa0UL));
            });
        }

        public ManualTimestamper Time { get; } = new();
        public SlotClock Clock { get; }
        public IBeaconNodeValidatorApi Node { get; } = Substitute.For<IBeaconNodeValidatorApi>();
        public IValidatorSigner Signer { get; } = Substitute.For<IValidatorSigner>();
        public List<(ulong Validator, Address FeeRecipient)[]> Prepared { get; } = [];
        public List<(ulong Slot, BlsSignature RandaoReveal, Hash256 Graffiti, long AtMs)> Produced { get; } = [];
        public List<BeaconBlockHeader> SignedHeaders { get; } = [];
        public List<SignedBlockContents> Published { get; } = [];

        /// <summary>The slot whose block pays another fee recipient than the configured one.</summary>
        public ulong? OtherFeeRecipientAt { get; set; }

        public ProposerDutyRunner Runner(Func<TimeSpan, CancellationToken, Task> delay) =>
            new(new ValidatorClientConfig { Enabled = true, PublicKeys = [Key(1).ToString(), Key(2).ToString()], FeeRecipient = FeeRecipient.ToString() }, Node, Signer, _spec, Clock, LimboLogs.Instance)
            {
                Delay = delay,
            };

        /// <summary>Runs from the start of <paramref name="from"/> until the clock reaches the start of <paramref name="until"/>.</summary>
        public async Task RunFrom(ulong from, ulong until)
        {
            Time.Set(DateTimeOffset.FromUnixTimeMilliseconds(SlotStartMs(from)).UtcDateTime);
            _endMs = SlotStartMs(until);
            using CancellationTokenSource driverStopping = new();
            using ProposerDutyRunner runner = Runner((delay, cancellationToken) =>
            {
                Time.Add(delay);
                if (Time.UtcNowOffset.ToUnixTimeMilliseconds() >= _endMs) driverStopping.Cancel();
                return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
            });
            await runner.Start(driverStopping.Token).WaitAsync(RunBound);
        }
    }
}
