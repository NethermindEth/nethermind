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
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using static Nethermind.BeaconChain.Test.Api.BeaconApiTestHost;

namespace Nethermind.BeaconChain.Test.ValidatorClient;

// Exact process-wide metric deltas require fixture isolation.
[NonParallelizable]
public class AttesterDutyRunnerTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ulong Epoch = Spec.FuluForkEpoch + 10;
    /// <summary>The last slot of <see cref="Epoch"/>.</summary>
    private static readonly ulong LastSlot = (Epoch + 1) * Spec.SlotsPerEpoch - 1;
    private static readonly ulong FailingSlot = LastSlot - 5;
    private static readonly long AttestationDueMs = (long)(Spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Attests_every_duty_once_at_a_third_of_its_slot_across_an_epoch_boundary()
    {
        // Validators 1 and 2 attest in the epoch's last slot and 1 again in the next epoch's first slot; 3 enters the registry by then.
        Harness harness = new(Spec, [(1, LastSlot), (2, LastSlot), (1, LastSlot + 1), (3, LastSlot + 2)]);
        int lookups = 0;
        harness.Node.Configure().GetValidators(default!, default).ReturnsForAnyArgs(call => ++lookups == 1
            ? throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, "The head state is not captured yet.")
            : new ValueTask<IReadOnlyList<ValidatorSummary>>(Registered(call.ArgAt<IReadOnlyList<BlsPublicKey>>(0), lookups == 2 ? 2UL : 3UL)));
        // Validator 1 cannot sign before validator 2 is asked, so a sequential signer loop never finishes.
        TaskCompletionSource<BlsSignature> firstSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Signer.Configure().SignAttestation(Key(1), Arg.Is<AttestationData>(data => data.Target!.Epoch == Epoch), Arg.Any<CancellationToken>()).Returns(new ValueTask<BlsSignature>(firstSignature.Task));
        harness.Signer.Configure().SignAttestation(Key(2), Arg.Any<AttestationData>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            firstSignature.SetResult(SignatureOf(1));
            return new ValueTask<BlsSignature>(SignatureOf(2));
        });
        ulong submittedBefore = Metrics.BeaconChainValidatorAttestationsSubmitted;

        await harness.RunFrom(LastSlot - 1, until: LastSlot + 3);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted, Is.EqualTo(new[]
        {
            new[] { Attestation(LastSlot, 1), Attestation(LastSlot, 2) },
            [Attestation(LastSlot + 1, 1)],
            [Attestation(LastSlot + 2, 3)],
        }));
        Assert.That(harness.Produced, Is.EqualTo(new[] { LastSlot, LastSlot + 1, LastSlot + 2 }.Select(static slot => (slot, 0UL, SlotStartMs(slot) + AttestationDueMs))));
        Assert.That(lookups, Is.EqualTo(3), "a refused lookup is retried next slot, and a key not yet registered is looked up again next epoch");
        Assert.That(harness.AskedBeyondNextEpoch, Is.False, "the node refuses duties after the epoch following the wall clock's");
        Assert.That(Metrics.BeaconChainValidatorAttestationsSubmitted - submittedBefore, Is.EqualTo(4UL));
    }

    [Test]
    public async Task A_reorg_that_moves_duties_within_their_epoch_is_followed_without_signing_a_target_twice()
    {
        Harness harness = new(Spec, [(1, FailingSlot), (2, FailingSlot + 2)]);
        harness.AfterSubmit = () =>
        {
            harness.Duties = [(1, FailingSlot + 1), (2, FailingSlot + 1)];
            harness.DependentRoot = FilledHash(0x02);
        };
        long alreadySignedBefore = Skipped("AlreadySigned");

        await harness.RunFrom(FailingSlot, until: FailingSlot + 3);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted, Is.EqualTo(new[] { new[] { Attestation(FailingSlot, 1) }, [Attestation(FailingSlot + 1, 2)] }));
        Assert.That(harness.Signer.ReceivedCalls().Count(), Is.EqualTo(2));
        Assert.That(Skipped("AlreadySigned") - alreadySignedBefore, Is.EqualTo(1));
    }

    private static IEnumerable<TestCaseData> FailedDuties()
    {
        yield return Failure("Syncing", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: harness.Clock.CurrentSlot == FailingSlot, optimistic: false)));
        yield return Failure("Optimistic", static harness => harness.Node.Configure().GetSyncingStatus(default).ReturnsForAnyArgs(_ => Status(syncing: false, optimistic: harness.Clock.CurrentSlot == FailingSlot)));
        yield return Failure("NotReady", static harness => harness.Node.Configure().ProduceAttestationData(FailingSlot, Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Throws(new BeaconNodeApiException(BeaconNodeApiError.NotReady, "syncing")));
        yield return Failure("NotSupported", static harness => harness.Node.Configure().ProduceAttestationData(FailingSlot, Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Throws(new BeaconNodeApiException(BeaconNodeApiError.NotSupported, "Gloas")));
        yield return Failure("Failed", static harness => harness.Node.Configure().ProduceAttestationData(FailingSlot, Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("a bug")));
        yield return Failure("SignerRefused", static harness => harness.Signer.Configure().SignAttestation(Key(1), Arg.Any<AttestationData>(), Arg.Any<CancellationToken>()).Throws(new ValidatorSignerException("slashable", refused: true, innerException: null)));
        yield return Failure("SignerUnreachable", static harness => harness.Signer.Configure().SignAttestation(Key(1), Arg.Any<AttestationData>(), Arg.Any<CancellationToken>()).Throws(new ValidatorSignerException("timed out", refused: false, innerException: null)));
        yield return Failure("RefusedOnSubmit", static harness => harness.Node.Configure().SubmitAttestations(Arg.Is<IReadOnlyList<SingleAttestation>>(batch => batch[0].Data!.Slot == FailingSlot), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<SubmissionFailure>>([new SubmissionFailure(0, "The signature is invalid.")])));

        static TestCaseData Failure(string reason, Action<Harness> arrange) => new TestCaseData(reason, arrange).SetName($"Skipped as {reason}");
    }

    [TestCaseSource(nameof(FailedDuties))]
    public async Task A_failed_duty_is_skipped_with_its_reason_and_the_next_slot_still_attests(string reason, Action<Harness> arrange)
    {
        Harness harness = new(Spec, [(1, FailingSlot), (2, FailingSlot + 1)]);
        arrange(harness);
        (long skippedBefore, ulong signerFailuresBefore, ulong submittedBefore) = (Skipped(reason), Metrics.BeaconChainValidatorSignerFailures, Metrics.BeaconChainValidatorAttestationsSubmitted);

        await harness.RunFrom(FailingSlot, until: FailingSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted[^1], Is.EqualTo(new[] { Attestation(FailingSlot + 1, 2) }));
        Assert.That(Skipped(reason) - skippedBefore, Is.EqualTo(1));
        Assert.That(Metrics.BeaconChainValidatorSignerFailures - signerFailuresBefore, Is.EqualTo(reason.StartsWith("Signer", StringComparison.Ordinal) ? 1UL : 0UL));
        Assert.That(Metrics.BeaconChainValidatorAttestationsSubmitted - submittedBefore, Is.EqualTo(1UL));
    }

    [Test]
    public async Task Duties_of_a_Gloas_epoch_are_neither_fetched_nor_performed()
    {
        Harness harness = new(Spec with { GloasForkEpoch = Epoch + 1 }, [(1, LastSlot), (1, LastSlot + 1)]);

        await harness.RunFrom(LastSlot, until: LastSlot + 2);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(harness.Submitted, Is.EqualTo(new[] { new[] { Attestation(LastSlot, 1) } }));
        await harness.Node.DidNotReceive().GetAttesterDuties(Arg.Is<ulong>(epoch => epoch > Epoch), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Stops_promptly_when_the_driver_stands_down_or_the_node_shuts_down([Values] bool driverStandsDown)
    {
        Harness harness = new(Spec, [(1, FailingSlot)]);
        harness.Time.Set(DateTimeOffset.FromUnixTimeMilliseconds(SlotStartMs(FailingSlot)).UtcDateTime);
        using CancellationTokenSource driverStopping = new();
        using AttesterDutyRunner runner = harness.Runner(static (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));

        Task run = runner.Start(driverStopping.Token);
        if (driverStandsDown) await driverStopping.CancelAsync();
        else await runner.StopAsync();

        Assert.That(async () => await run.WaitAsync(RunBound), Throws.Nothing);
        await harness.Node.DidNotReceiveWithAnyArgs().ProduceAttestationData(default, default, default);
    }

    private static long SlotStartMs(ulong slot) => (long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot) * 1000;

    /// <summary>The key of the validator at registry index <paramref name="index"/>.</summary>
    private static BlsPublicKey Key(ulong index) => FilledPubkey((byte)(0xa0 + index));

    private static BlsSignature SignatureOf(ulong index) => FilledSignature((byte)(0xb0 + index));

    /// <summary>What validator <paramref name="index"/> submits for <paramref name="slot"/>; its committee index is its validator index.</summary>
    private static (ulong Slot, ulong Validator, ulong Committee, BlsSignature Signature) Attestation(ulong slot, ulong index) => (slot, index, index, SignatureOf(index));

    private static long Skipped(string reason) => Metrics.BeaconChainValidatorAttestationsSkipped.GetValueOrDefault(new StringLabel(reason));

    private static ValueTask<SyncingStatus> Status(bool syncing, bool optimistic) => new(new SyncingStatus(0, syncing ? 64UL : 0UL, syncing, optimistic, ExecutionLayerOffline: false));

    /// <returns>The entries of <paramref name="keys"/> among validators 1 to <paramref name="registeredCount"/>.</returns>
    private static IReadOnlyList<ValidatorSummary> Registered(IReadOnlyList<BlsPublicKey> keys, ulong registeredCount)
    {
        List<ValidatorSummary> found = [];
        for (ulong index = 1; index <= registeredCount; index++)
        {
            if (keys.Contains(Key(index))) found.Add(new ValidatorSummary(Key(index), index, 0, Presets.FarFutureEpoch, Slashed: false));
        }

        return found;
    }

    /// <summary>A node and signer that serve <see cref="Duties"/> to validators 1 to 3, on a clock that only moves when the runner waits.</summary>
    public sealed class Harness
    {
        private readonly BeaconChainSpec _spec;
        private long _endMs;

        public Harness(BeaconChainSpec spec, (ulong Validator, ulong Slot)[] duties)
        {
            _spec = spec;
            Duties = duties;
            Clock = new SlotClock(spec, Time);
            Node.GetSyncingStatus(default).ReturnsForAnyArgs(Status(syncing: false, optimistic: false));
            Node.GetValidators(default!, default).ReturnsForAnyArgs(call => new ValueTask<IReadOnlyList<ValidatorSummary>>(Registered(call.ArgAt<IReadOnlyList<BlsPublicKey>>(0), 3)));
            Node.GetAttesterDuties(default, default!, default).ReturnsForAnyArgs(call =>
            {
                AskedBeyondNextEpoch |= call.ArgAt<ulong>(0) > Clock.CurrentEpoch + 1;
                return new ValueTask<DutiesResult<AttesterDuty>>(DutiesOf(call.ArgAt<ulong>(0), call.ArgAt<IReadOnlyList<ulong>>(1)));
            });
            Node.ProduceAttestationData(default, default, default).ReturnsForAnyArgs(call =>
            {
                ulong slot = call.ArgAt<ulong>(0);
                Produced.Add((slot, call.ArgAt<ulong>(1), Time.UtcNowOffset.ToUnixTimeMilliseconds()));
                return new ValueTask<AttestationData>(new AttestationData
                {
                    Slot = slot,
                    BeaconBlockRoot = FilledHash(0x10),
                    Source = new Checkpoint { Epoch = spec.GetEpoch(slot) - 1, Root = FilledHash(0x11) },
                    Target = new Checkpoint { Epoch = spec.GetEpoch(slot), Root = FilledHash(0x12) },
                });
            });
            Node.SubmitAttestations(default!, default).ReturnsForAnyArgs(call =>
            {
                Submitted.Add([.. call.ArgAt<IReadOnlyList<SingleAttestation>>(0).Select(static attestation => (attestation.Data!.Slot, attestation.AttesterIndex, attestation.CommitteeIndex, attestation.Signature))]);
                AfterSubmit?.Invoke();
                return new ValueTask<IReadOnlyList<SubmissionFailure>>([]);
            });
            Signer.SignAttestation(default, default!, default).ReturnsForAnyArgs(call => new ValueTask<BlsSignature>(SignatureOf(call.ArgAt<BlsPublicKey>(0).Bytes[0] - 0xa0UL)));
        }

        public ManualTimestamper Time { get; } = new();
        public SlotClock Clock { get; }
        public IBeaconNodeValidatorApi Node { get; } = Substitute.For<IBeaconNodeValidatorApi>();
        public IValidatorSigner Signer { get; } = Substitute.For<IValidatorSigner>();
        public (ulong Validator, ulong Slot)[] Duties { get; set; }
        public Hash256 DependentRoot { get; set; } = FilledHash(0x01);
        public Action? AfterSubmit { get; set; }
        public bool AskedBeyondNextEpoch { get; private set; }
        public List<(ulong Slot, ulong CommitteeIndex, long AtMs)> Produced { get; } = [];
        public List<(ulong Slot, ulong Validator, ulong Committee, BlsSignature Signature)[]> Submitted { get; } = [];

        public AttesterDutyRunner Runner(Func<TimeSpan, CancellationToken, Task> delay) =>
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
            using AttesterDutyRunner runner = Runner((delay, cancellationToken) =>
            {
                Time.Add(delay);
                if (Time.UtcNowOffset.ToUnixTimeMilliseconds() >= _endMs) driverStopping.Cancel();
                return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
            });
            await runner.Start(driverStopping.Token).WaitAsync(RunBound);
        }

        private DutiesResult<AttesterDuty> DutiesOf(ulong epoch, IReadOnlyList<ulong> validatorIndices)
        {
            List<AttesterDuty> duties = [];
            foreach ((ulong validator, ulong slot) in Duties)
            {
                if (_spec.GetEpoch(slot) == epoch && validatorIndices.Contains(validator))
                    duties.Add(new AttesterDuty(Key(validator), validator, validator, 128, 64, 0, slot));
            }

            return new DutiesResult<AttesterDuty>(DependentRoot, ExecutionOptimistic: false, duties);
        }
    }
}
