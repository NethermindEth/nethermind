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

/// <summary>Performs the sync committee duties of the configured validators, talking to the node only through <see cref="IBeaconNodeValidatorApi"/>.</summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="AttesterDutyRunner"/> with its own loop, so a slow node or signer here never delays an attestation. Once an epoch
/// it looks up keys not yet in the registry, fetches the sync duties of the current sync committee period and, from
/// <see cref="NextPeriodLeadEpochs"/> before its end, of the next one, and subscribes the node to the subnets of every validator on duty
/// until its period ends. A fetch or subscription that fails is tried again next slot.
/// </para>
/// <para>
/// A committee signs from the last slot before its period until the last slot but one of it (altair/validator.md "Sync Committee"), so the
/// message of a slot is signed by the committee of the next slot's period. A third into each slot the validators on duty sign the head
/// block root, and the messages are submitted in one call. Two thirds in, each signs a selection proof for each subcommittee it is in;
/// each one selected signs the node's contribution of its subcommittee for that root, and the contributions are submitted in one call.
/// </para>
/// <para>A duty that fails, for whatever reason, is skipped, logged and counted; the next slot runs regardless. Gloas slots are not performed.</para>
/// </remarks>
public sealed class SyncCommitteeDutyRunner(
    IValidatorClientConfig config,
    IBeaconNodeValidatorApi node,
    IValidatorSigner signer,
    BeaconChainSpec spec,
    SlotClock slotClock,
    ILogManager logManager) : IStoppableService, IDisposable
{
    /// <summary>
    /// The epochs before a sync committee period from which its duties are fetched and its subnets joined: the most of the 1 to
    /// <c>SYNC_COMMITTEE_SUBNET_COUNT</c> epochs altair/validator.md "Sync committee subnet stability" draws from.
    /// </summary>
    internal const ulong NextPeriodLeadEpochs = SyncCommitteeContribution.SubnetCount;

    private readonly ILogger _logger = logManager.GetClassLogger<SyncCommitteeDutyRunner>();
    private readonly ValidatorIndexLookup _validators = new(config, node, logManager.GetClassLogger<SyncCommitteeDutyRunner>());
    private readonly long _syncMessageDueMilliseconds = (long)(spec.SecondsPerSlot * 1000 * GloasTiming.SyncMessageDueBps / Presets.BasisPoints);
    private readonly long _contributionDueMilliseconds = (long)(spec.SecondsPerSlot * 1000 * GloasTiming.ContributionDueBps / Presets.BasisPoints);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, IReadOnlyList<SyncDuty>> _dutiesByPeriod = [];
    private ulong? _preparedEpoch;
    private CancellationTokenRegistration _driverStopped;
    private Task _run = Task.CompletedTask;

    /// <summary>Waits out a delay; tests replace it to advance a manual clock instead of sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    string IStoppableService.Description => "sync committee duties";

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
        if (_logger.IsInfo) _logger.Info($"Performing sync committee duties for {_validators.KeyCount} validator keys");
        try
        {
            for (ulong slot = slotClock.CurrentSlot; ; slot = Math.Max(slot + 1, slotClock.CurrentSlot))
            {
                await PrepareAsync(slot, cancellationToken);
                await WaitUntil(slotClock.SlotStartMilliseconds(slot) + _syncMessageDueMilliseconds, cancellationToken);
                IReadOnlyList<SyncDuty> duties = DutiesAt(slot);
                if (duties.Count == 0) continue;

                Hash256? blockRoot = await SignMessagesAsync(slot, duties, cancellationToken);
                await WaitUntil(slotClock.SlotStartMilliseconds(slot) + _contributionDueMilliseconds, cancellationToken);
                await ContributeAsync(slot, blockRoot, duties, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (_logger.IsInfo) _logger.Info("Sync committee duties stopped");
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
        ulong period = epoch / Presets.EpochsPerSyncCommitteePeriod;
        foreach (ulong knownPeriod in _dutiesByPeriod.Keys)
        {
            if (knownPeriod < period) _dutiesByPeriod.Remove(knownPeriod);
        }

        await _validators.Update(epoch, cancellationToken);
        if (_validators.Indices.Length == 0 || _preparedEpoch == epoch) return;

        bool prepared = true;
        List<SyncCommitteeSubscription> subscriptions = [];
        ulong lastPeriod = epoch + NextPeriodLeadEpochs >= (period + 1) * Presets.EpochsPerSyncCommitteePeriod ? period + 1 : period;
        for (ulong dutyPeriod = period; dutyPeriod <= lastPeriod; dutyPeriod++)
        {
            ulong dutyEpoch = Math.Max(epoch, dutyPeriod * Presets.EpochsPerSyncCommitteePeriod);
            // The node refuses Gloas duties; this client performs duties up to Fulu.
            if (dutyEpoch >= spec.GloasForkEpoch) break;
            try
            {
                DutiesResult<SyncDuty> duties = await node.GetSyncDuties(dutyEpoch, _validators.Indices, cancellationToken);
                _dutiesByPeriod[dutyPeriod] = duties.Duties;
                foreach (SyncDuty duty in duties.Duties)
                    subscriptions.Add(new SyncCommitteeSubscription(duty.ValidatorIndex, duty.ValidatorSyncCommitteeIndices, (dutyPeriod + 1) * Presets.EpochsPerSyncCommitteePeriod));
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                prepared = false;
                DutyCalls.LogFailure(_logger, $"Fetching the sync committee duties of period {dutyPeriod}", e);
            }
        }

        if (subscriptions.Count > 0)
        {
            try
            {
                await node.SubscribeToSyncCommittees(subscriptions, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                prepared = false;
                DutyCalls.LogFailure(_logger, "Subscribing to the sync committee subnets", e);
            }
        }

        if (prepared) _preparedEpoch = epoch;
    }

    /// <summary>The duties of the validators that sign the message of <paramref name="slot"/>: those of the committee of the next slot's period.</summary>
    private IReadOnlyList<SyncDuty> DutiesAt(ulong slot) =>
        spec.GetEpoch(slot) < spec.GloasForkEpoch && _dutiesByPeriod.TryGetValue(spec.GetEpoch(slot + 1) / Presets.EpochsPerSyncCommitteePeriod, out IReadOnlyList<SyncDuty>? duties)
            ? duties
            : [];

    /// <summary>Has every validator on duty sign the head block root and submits the messages in one call (altair/validator.md "Sync committee messages").</summary>
    /// <returns>The block root signed, or <c>null</c> when it was not read.</returns>
    private async Task<Hash256?> SignMessagesAsync(ulong slot, IReadOnlyList<SyncDuty> duties, CancellationToken cancellationToken)
    {
        Hash256? blockRoot = null;
        try
        {
            if (await DutyCalls.RefusalToPerform(node, slotClock, slot, cancellationToken) is { } refusal)
            {
                SkipMessages(refusal.Reason, duties.Count, refusal.Detail);
                return null;
            }

            Hash256 headRoot = await node.GetHeadBlockRoot(cancellationToken);
            blockRoot = headRoot;
            Task<BlsSignature?>[] signing = new Task<BlsSignature?>[duties.Count];
            for (int i = 0; i < signing.Length; i++)
            {
                SyncDuty duty = duties[i];
                signing[i] = DutyCalls.SignAsync(() => signer.SignSyncCommitteeMessage(duty.PublicKey, slot, headRoot, cancellationToken), SkipMessages);
            }

            BlsSignature?[] signatures = await Task.WhenAll(signing);
            List<SyncCommitteeMessage> messages = [];
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i] is { } signature)
                    messages.Add(new SyncCommitteeMessage { Slot = slot, BeaconBlockRoot = headRoot, ValidatorIndex = duties[i].ValidatorIndex, Signature = signature });
            }

            if (messages.Count == 0) return headRoot;
            IReadOnlyList<SubmissionFailure> failures = await node.SubmitSyncCommitteeMessages(messages, cancellationToken);
            foreach (SubmissionFailure failure in failures)
                SkipMessages("RefusedOnSubmit", 1, $"the node refused the sync committee message of validator {messages[failure.Index].ValidatorIndex}: {failure.Message}");

            Interlocked.Add(ref Metrics.ValidatorSyncCommitteeMessagesSubmittedCount, (ulong)(messages.Count - failures.Count));
            if (_logger.IsInfo) _logger.Info($"Submitted {messages.Count - failures.Count} sync committee messages for slot {slot}");
            return headRoot;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SkipMessages(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", duties.Count, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Sync committee messages of slot {slot} failed unexpectedly.", e);
            return blockRoot;
        }
    }

    /// <summary>
    /// Has each validator on duty sign a selection proof for each of its subcommittees, and each one selected sign the node's contribution
    /// of the subcommittee for <paramref name="blockRoot"/>, and submits them in one call (altair/validator.md "Sync committee contributions").
    /// </summary>
    /// <param name="blockRoot">The block root the messages of the slot signed, or <c>null</c> when it was not read, which leaves nothing to aggregate.</param>
    private async Task ContributeAsync(ulong slot, Hash256? blockRoot, IReadOnlyList<SyncDuty> duties, CancellationToken cancellationToken)
    {
        List<(SyncDuty Duty, ulong SubcommitteeIndex)> memberships = [];
        foreach (SyncDuty duty in duties)
        {
            HashSet<ulong> subcommittees = [];
            foreach (ulong position in duty.ValidatorSyncCommitteeIndices)
            {
                if (subcommittees.Add(position / SyncCommitteeContribution.SubcommitteeSize)) memberships.Add((duty, position / SyncCommitteeContribution.SubcommitteeSize));
            }
        }

        Task<BlsSignature?>[] proving = new Task<BlsSignature?>[memberships.Count];
        for (int i = 0; i < proving.Length; i++)
        {
            (SyncDuty duty, ulong subcommitteeIndex) = memberships[i];
            proving[i] = DutyCalls.SignAsync(() => signer.SignSyncCommitteeSelectionProof(duty.PublicKey, slot, subcommitteeIndex, cancellationToken), (_, _, detail) =>
            {
                if (_logger.IsWarn) _logger.Warn($"No selection proof for validator {duty.ValidatorIndex} in sync subcommittee {subcommitteeIndex} at slot {slot}: {detail}");
            });
        }

        BlsSignature?[] selectionProofs = await Task.WhenAll(proving);
        List<(SyncDuty Duty, ulong SubcommitteeIndex, BlsSignature SelectionProof)> aggregations = [];
        for (int i = 0; i < selectionProofs.Length; i++)
        {
            if (selectionProofs[i] is { } selectionProof && BeaconStateAccessors.IsSyncCommitteeAggregator(selectionProof))
                aggregations.Add((memberships[i].Duty, memberships[i].SubcommitteeIndex, selectionProof));
        }

        if (aggregations.Count == 0) return;
        if (blockRoot is null)
        {
            SkipContributions("NoBlockRoot", aggregations.Count, $"slot {slot} has no block root to aggregate the messages of");
            return;
        }

        try
        {
            if (await DutyCalls.RefusalToPerform(node, slotClock, slot, cancellationToken) is { } refusal)
            {
                SkipContributions(refusal.Reason, aggregations.Count, refusal.Detail);
                return;
            }

            Dictionary<ulong, SyncCommitteeContribution?> contributions = [];
            List<(SyncDuty Duty, ContributionAndProof ContributionAndProof)> signing = [];
            foreach ((SyncDuty duty, ulong subcommitteeIndex, BlsSignature selectionProof) in aggregations)
            {
                if (!contributions.TryGetValue(subcommitteeIndex, out SyncCommitteeContribution? contribution))
                    contributions[subcommitteeIndex] = contribution = await GetContributionAsync(slot, subcommitteeIndex, blockRoot, cancellationToken);
                if (contribution is null)
                    SkipContributions("NotFound", 1, $"the node holds no sync committee message of subcommittee {subcommitteeIndex} at slot {slot} for validator {duty.ValidatorIndex} to aggregate");
                else
                    signing.Add((duty, new ContributionAndProof { AggregatorIndex = duty.ValidatorIndex, Contribution = contribution, SelectionProof = selectionProof }));
            }

            Task<BlsSignature?>[] signingTasks = new Task<BlsSignature?>[signing.Count];
            for (int i = 0; i < signingTasks.Length; i++)
            {
                (SyncDuty duty, ContributionAndProof contributionAndProof) = signing[i];
                signingTasks[i] = DutyCalls.SignAsync(() => signer.SignContributionAndProof(duty.PublicKey, contributionAndProof, cancellationToken), SkipContributions);
            }

            BlsSignature?[] signatures = await Task.WhenAll(signingTasks);
            List<SignedContributionAndProof> signed = [];
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i] is { } signature) signed.Add(new SignedContributionAndProof { Message = signing[i].ContributionAndProof, Signature = signature });
            }

            if (signed.Count == 0) return;
            IReadOnlyList<SubmissionFailure> failures = await node.SubmitContributionAndProofs(signed, cancellationToken);
            foreach (SubmissionFailure failure in failures)
                SkipContributions("RefusedOnSubmit", 1, $"the node refused the contribution of validator {signed[failure.Index].Message!.AggregatorIndex}: {failure.Message}");

            Interlocked.Add(ref Metrics.ValidatorSyncCommitteeContributionsSubmittedCount, (ulong)(signed.Count - failures.Count));
            if (_logger.IsInfo) _logger.Info($"Submitted {signed.Count - failures.Count} sync committee contributions for slot {slot}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SkipContributions(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", aggregations.Count, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Sync committee contributions of slot {slot} failed unexpectedly.", e);
        }
    }

    private async Task<SyncCommitteeContribution?> GetContributionAsync(ulong slot, ulong subcommitteeIndex, Hash256 blockRoot, CancellationToken cancellationToken)
    {
        try
        {
            return await node.ProduceSyncCommitteeContribution(slot, subcommitteeIndex, blockRoot, cancellationToken);
        }
        catch (BeaconNodeApiException e) when (e.Error == BeaconNodeApiError.NotFound)
        {
            return null;
        }
    }

    private void SkipMessages(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorSyncCommitteeMessagesSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} sync committee message duties: {detail}");
    }

    private void SkipContributions(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorSyncCommitteeContributionsSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} sync committee aggregation duties: {detail}");
    }
}
