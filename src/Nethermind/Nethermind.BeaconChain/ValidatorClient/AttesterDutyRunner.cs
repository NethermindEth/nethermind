// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Performs the attester and aggregation duties of the configured validators, talking to the node only through <see cref="IBeaconNodeValidatorApi"/>.</summary>
/// <remarks>
/// <para>
/// Each slot it first looks up keys not yet in the registry (once an epoch) and polls the duties of the slot's epoch and the
/// next, replacing them whenever the node answers, which is how a re-org that changes their dependent root is picked up.
/// At the attestation deadline, a third into the slot, it checks the node is synced, asks for the slot's attestation data
/// once, has every validator with a duty in the slot sign it concurrently and submits the signed attestations in one call.
/// </para>
/// <para>
/// Aggregation rides on the same duties, so they are fetched in one place. Once a duty is known, its selection proof is signed
/// and the node told of the committee, as aggregator or not (phase0/validator.md "Lookahead"), usually an epoch ahead, so the node
/// joins an aggregator's subnet in time. At the aggregation deadline, two thirds into the slot, each aggregator of the slot signs
/// the node's best aggregate of its committee for the data attested to, and the aggregates are submitted in one call.
/// </para>
/// <para>
/// A validator is never asked to sign for a target epoch at or before one it was already asked to sign for. That guard lives
/// in memory only and is lost on restart; the remote signer's slashing protection is the durable one.
/// </para>
/// <para>A duty that fails, for whatever reason, is skipped, logged and counted; the next slot runs regardless.</para>
/// </remarks>
public sealed class AttesterDutyRunner(
    IValidatorClientConfig config,
    IBeaconNodeValidatorApi node,
    IValidatorSigner signer,
    BeaconChainSpec spec,
    SlotClock slotClock,
    ILogManager logManager) : IStoppableService, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<AttesterDutyRunner>();
    private readonly ValidatorIndexLookup _validators = new(config, node, logManager.GetClassLogger<AttesterDutyRunner>());
    private readonly long _attestationDueMilliseconds = (long)(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);
    private readonly long _aggregateDueMilliseconds = (long)(spec.SecondsPerSlot * 1000 * GloasTiming.AggregateDueBps / Presets.BasisPoints);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, DutiesResult<AttesterDuty>> _dutiesByEpoch = [];
    private readonly Dictionary<ulong, ulong> _lastSignedTargetEpochs = [];

    // The duties the node was told of, with the selection proof of each one that aggregates.
    private readonly Dictionary<AttesterDuty, BlsSignature?> _subscribedDuties = [];
    private CancellationTokenRegistration _driverStopped;
    private Task _run = Task.CompletedTask;

    /// <summary>Waits out a delay; tests replace it to advance a manual clock instead of sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    string IStoppableService.Description => "attester and aggregation duties";

    /// <summary>Performs the duties in the background until <see cref="StopAsync"/> is called or <paramref name="driverStopping"/> is cancelled.</summary>
    /// <returns>The background run, which ends without faulting once stopped.</returns>
    public Task Start(CancellationToken driverStopping)
    {
        CancellationToken cancellationToken = _stopping.Token;
        _driverStopped = driverStopping.Register(static state => _ = ((CancellationTokenSource)state!).CancelAsync(), _stopping);
        return _run = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
    }

    /// <summary>Stops the duties and waits until no call to the node or the signer is in flight.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync();
        await _run;
    }

    public void Dispose()
    {
        _driverStopped.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info($"Performing attester duties for {_validators.KeyCount} validator keys");
        if (spec.GloasForkEpoch != Presets.FarFutureEpoch && _logger.IsWarn) _logger.Warn($"Attester duties stop at the Gloas fork epoch {spec.GloasForkEpoch}: Gloas duties are not supported yet");
        try
        {
            for (ulong slot = slotClock.CurrentSlot; ; slot = Math.Max(slot + 1, slotClock.CurrentSlot))
            {
                await PrepareAsync(slot, cancellationToken);
                await WaitUntil(slotClock.SlotStartMilliseconds(slot) + _attestationDueMilliseconds, cancellationToken);
                AttestationData? data = await AttestAsync(slot, cancellationToken);

                List<(AttesterDuty Duty, BlsSignature SelectionProof)> aggregations = [];
                foreach (AttesterDuty duty in DutiesAt(slot))
                {
                    if (_subscribedDuties.GetValueOrDefault(duty) is { } selectionProof) aggregations.Add((duty, selectionProof));
                }

                if (aggregations.Count == 0) continue;
                await WaitUntil(slotClock.SlotStartMilliseconds(slot) + _aggregateDueMilliseconds, cancellationToken);
                await AggregateAsync(slot, data, aggregations, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (_logger.IsInfo) _logger.Info("Attester duties stopped");
    }

    private async Task WaitUntil(long unixMilliseconds, CancellationToken cancellationToken)
    {
        // Timers can fire marginally early, and a duty must not be performed before its deadline.
        for (long remaining = unixMilliseconds - slotClock.UnixMilliseconds; remaining > 0; remaining = unixMilliseconds - slotClock.UnixMilliseconds)
            await Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);
    }

    private async Task PrepareAsync(ulong slot, CancellationToken cancellationToken)
    {
        ulong epoch = spec.GetEpoch(slot);
        foreach (ulong knownEpoch in _dutiesByEpoch.Keys)
        {
            if (knownEpoch < epoch) _dutiesByEpoch.Remove(knownEpoch);
        }

        foreach (AttesterDuty subscribed in _subscribedDuties.Keys)
        {
            if (subscribed.Slot < slot) _subscribedDuties.Remove(subscribed);
        }

        await _validators.Update(epoch, cancellationToken);
        if (_validators.Indices.Length == 0) return;
        // An epoch's first slot is prepared during the previous epoch, when the node still refuses the duties of the epoch after it.
        ulong lastDutyEpoch = Math.Min(epoch + 1, slotClock.CurrentEpoch + 1);
        for (ulong dutyEpoch = epoch; dutyEpoch <= lastDutyEpoch; dutyEpoch++)
        {
            // The node refuses Gloas duties; this client performs duties up to Fulu.
            if (dutyEpoch >= spec.GloasForkEpoch) break;
            try
            {
                DutiesResult<AttesterDuty> duties = await node.GetAttesterDuties(dutyEpoch, _validators.Indices, cancellationToken);
                if (_dutiesByEpoch.TryGetValue(dutyEpoch, out DutiesResult<AttesterDuty>? known) && known.DependentRoot != duties.DependentRoot && _logger.IsInfo)
                    _logger.Info($"Attester duties of epoch {dutyEpoch} changed with their dependent root, from {known.DependentRoot} to {duties.DependentRoot}");
                _dutiesByEpoch[dutyEpoch] = duties;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                DutyCalls.LogFailure(_logger, $"Fetching the attester duties of epoch {dutyEpoch}", e);
            }
        }

        await SubscribeAsync(slot, cancellationToken);
    }

    /// <summary>Signs the selection proof of each duty from <paramref name="slot"/> on that the node was not told of yet, and subscribes the node to their committees.</summary>
    /// <remarks>A duty whose selection proof or subscription failed is tried again next slot.</remarks>
    private async Task SubscribeAsync(ulong slot, CancellationToken cancellationToken)
    {
        List<AttesterDuty> duties = [];
        foreach (DutiesResult<AttesterDuty> epochDuties in _dutiesByEpoch.Values)
        {
            foreach (AttesterDuty duty in epochDuties.Duties)
            {
                if (duty.Slot >= slot && !_subscribedDuties.ContainsKey(duty)) duties.Add(duty);
            }
        }

        if (duties.Count == 0) return;
        Task<BlsSignature?>[] signing = new Task<BlsSignature?>[duties.Count];
        for (int i = 0; i < signing.Length; i++)
        {
            AttesterDuty duty = duties[i];
            signing[i] = DutyCalls.SignAsync(() => signer.SignAggregationSlot(duty.PublicKey, duty.Slot, cancellationToken), (_, _, detail) =>
            {
                if (_logger.IsWarn) _logger.Warn($"No selection proof for the duty of validator {duty.ValidatorIndex} at slot {duty.Slot}; asking again next slot: {detail}");
            });
        }

        BlsSignature?[] selectionProofs = await Task.WhenAll(signing);
        List<BeaconCommitteeSubscription> subscriptions = [];
        Dictionary<AttesterDuty, BlsSignature?> subscribing = [];
        for (int i = 0; i < duties.Count; i++)
        {
            if (selectionProofs[i] is not { } selectionProof) continue;
            AttesterDuty duty = duties[i];
            bool aggregator = BeaconStateAccessors.IsAggregator((int)duty.CommitteeLength, selectionProof);
            subscriptions.Add(new BeaconCommitteeSubscription(duty.ValidatorIndex, duty.CommitteeIndex, duty.CommitteesAtSlot, duty.Slot, aggregator));
            subscribing[duty] = aggregator ? selectionProof : null;
        }

        if (subscriptions.Count == 0) return;
        try
        {
            await node.SubscribeToBeaconCommittees(subscriptions, cancellationToken);
            foreach ((AttesterDuty duty, BlsSignature? selectionProof) in subscribing) _subscribedDuties[duty] = selectionProof;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DutyCalls.LogFailure(_logger, "Subscribing to the committees of the attester duties", e);
        }
    }

    private List<AttesterDuty> DutiesAt(ulong slot)
    {
        List<AttesterDuty> duties = [];
        if (_dutiesByEpoch.TryGetValue(spec.GetEpoch(slot), out DutiesResult<AttesterDuty>? epochDuties))
        {
            foreach (AttesterDuty duty in epochDuties.Duties)
            {
                if (duty.Slot == slot) duties.Add(duty);
            }
        }

        return duties;
    }

    /// <returns>The attestation data of the slot, or <c>null</c> when none was produced.</returns>
    private async Task<AttestationData?> AttestAsync(ulong slot, CancellationToken cancellationToken)
    {
        List<AttesterDuty> duties = DutiesAt(slot);
        if (duties.Count == 0) return null;
        try
        {
            if (await DutyCalls.RefusalToPerform(node, slotClock, slot, cancellationToken) is { } refusal)
            {
                Skip(refusal.Reason, duties.Count, refusal.Detail);
                return null;
            }

            AttestationData data = await node.ProduceAttestationData(slot, 0, cancellationToken);
            ulong targetEpoch = data.Target!.Epoch;
            List<AttesterDuty> signing = [];
            foreach (AttesterDuty duty in duties)
            {
                if (_lastSignedTargetEpochs.TryGetValue(duty.ValidatorIndex, out ulong signedTargetEpoch) && signedTargetEpoch >= targetEpoch)
                {
                    Skip("AlreadySigned", 1, $"validator {duty.ValidatorIndex} was already asked to sign for target epoch {signedTargetEpoch}");
                    continue;
                }

                // Recorded before asking: a request that fails in transit may still have been signed.
                _lastSignedTargetEpochs[duty.ValidatorIndex] = targetEpoch;
                signing.Add(duty);
            }

            Task<BlsSignature?>[] signingTasks = new Task<BlsSignature?>[signing.Count];
            for (int i = 0; i < signingTasks.Length; i++)
            {
                AttesterDuty duty = signing[i];
                signingTasks[i] = DutyCalls.SignAsync(() => signer.SignAttestation(duty.PublicKey, data, cancellationToken), Skip);
            }

            BlsSignature?[] signatures = await Task.WhenAll(signingTasks);

            List<SingleAttestation> attestations = [];
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i] is { } signature)
                    attestations.Add(new SingleAttestation { CommitteeIndex = signing[i].CommitteeIndex, AttesterIndex = signing[i].ValidatorIndex, Data = data, Signature = signature });
            }

            if (attestations.Count > 0)
            {
                IReadOnlyList<SubmissionFailure> failures = await node.SubmitAttestations(attestations, cancellationToken);
                foreach (SubmissionFailure failure in failures)
                    Skip("RefusedOnSubmit", 1, $"the node refused the attestation of validator {attestations[failure.Index].AttesterIndex}: {failure.Message}");

                Interlocked.Add(ref Metrics.ValidatorAttestationsSubmittedCount, (ulong)(attestations.Count - failures.Count));
                if (_logger.IsInfo) _logger.Info($"Submitted {attestations.Count - failures.Count} attestations for slot {slot}");
            }

            return data;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Skip(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", duties.Count, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Attester duties of slot {slot} failed unexpectedly.", e);
            return null;
        }
    }

    /// <summary>Has each aggregator sign the node's best aggregate of its committee for <paramref name="data"/> and submits them in one call (phase0/validator.md "Broadcast aggregate").</summary>
    /// <param name="data">What the slot's attesters signed, or <c>null</c> when it was not produced, which leaves nothing to aggregate.</param>
    private async Task AggregateAsync(ulong slot, AttestationData? data, List<(AttesterDuty Duty, BlsSignature SelectionProof)> aggregations, CancellationToken cancellationToken)
    {
        if (data is null)
        {
            SkipAggregation("NoAttestationData", aggregations.Count, $"slot {slot} has no attestation data to aggregate");
            return;
        }

        try
        {
            if (await DutyCalls.RefusalToPerform(node, slotClock, slot, cancellationToken) is { } refusal)
            {
                SkipAggregation(refusal.Reason, aggregations.Count, refusal.Detail);
                return;
            }

            Hash256 dataRoot = SszRoots.HashTreeRoot(data);
            Dictionary<ulong, Attestation?> aggregates = [];
            List<(AttesterDuty Duty, AggregateAndProof AggregateAndProof)> signing = [];
            foreach ((AttesterDuty duty, BlsSignature selectionProof) in aggregations)
            {
                if (!aggregates.TryGetValue(duty.CommitteeIndex, out Attestation? aggregate))
                    aggregates[duty.CommitteeIndex] = aggregate = await GetAggregateAsync(slot, dataRoot, duty.CommitteeIndex, cancellationToken);
                if (aggregate is null)
                    SkipAggregation("NotFound", 1, $"the node holds no attestation of committee {duty.CommitteeIndex} at slot {slot} for validator {duty.ValidatorIndex} to aggregate");
                else
                    signing.Add((duty, new AggregateAndProof { AggregatorIndex = duty.ValidatorIndex, Aggregate = aggregate, SelectionProof = selectionProof }));
            }

            Task<BlsSignature?>[] signingTasks = new Task<BlsSignature?>[signing.Count];
            for (int i = 0; i < signingTasks.Length; i++)
            {
                (AttesterDuty duty, AggregateAndProof aggregateAndProof) = signing[i];
                signingTasks[i] = DutyCalls.SignAsync(() => signer.SignAggregateAndProof(duty.PublicKey, aggregateAndProof, cancellationToken), SkipAggregation);
            }

            BlsSignature?[] signatures = await Task.WhenAll(signingTasks);
            List<SignedAggregateAndProof> signed = [];
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i] is { } signature) signed.Add(new SignedAggregateAndProof { Message = signing[i].AggregateAndProof, Signature = signature });
            }

            if (signed.Count == 0) return;
            IReadOnlyList<SubmissionFailure> failures = await node.SubmitAggregateAndProofs(signed, cancellationToken);
            foreach (SubmissionFailure failure in failures)
                SkipAggregation("RefusedOnSubmit", 1, $"the node refused the aggregate of validator {signed[failure.Index].Message!.AggregatorIndex}: {failure.Message}");

            Interlocked.Add(ref Metrics.ValidatorAggregatesSubmittedCount, (ulong)(signed.Count - failures.Count));
            if (_logger.IsInfo) _logger.Info($"Submitted {signed.Count - failures.Count} aggregates for slot {slot}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SkipAggregation(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", aggregations.Count, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Aggregation duties of slot {slot} failed unexpectedly.", e);
        }
    }

    private async Task<Attestation?> GetAggregateAsync(ulong slot, Hash256 dataRoot, ulong committeeIndex, CancellationToken cancellationToken)
    {
        try
        {
            return await node.GetAggregateAttestation(slot, dataRoot, committeeIndex, cancellationToken);
        }
        catch (BeaconNodeApiException e) when (e.Error == BeaconNodeApiError.NotFound)
        {
            return null;
        }
    }

    private void Skip(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorAttestationsSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} attester duties: {detail}");
    }

    private void SkipAggregation(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorAggregationsSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} aggregation duties: {detail}");
    }
}
