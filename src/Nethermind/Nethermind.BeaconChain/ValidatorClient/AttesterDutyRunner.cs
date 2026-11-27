// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Config;
using Nethermind.Core.Attributes;
using Nethermind.Core.Exceptions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Performs the attester duties of the configured validators, talking to the node only through <see cref="IBeaconNodeValidatorApi"/>.</summary>
/// <remarks>
/// <para>
/// Each slot it first looks up keys not yet in the registry (once an epoch) and polls the duties of the slot's epoch and the
/// next, replacing them whenever the node answers, which is how a re-org that changes their dependent root is picked up.
/// At the attestation deadline, a third into the slot, it checks the node is synced, asks for the slot's attestation data
/// once, has every validator with a duty in the slot sign it concurrently and submits the signed attestations in one call.
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
    private readonly long _attestationDueMilliseconds = (long)(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, DutiesResult<AttesterDuty>> _dutiesByEpoch = [];
    private readonly Dictionary<ulong, ulong> _lastSignedTargetEpochs = [];
    private BlsPublicKey[] _unresolvedKeys = ParsePublicKeys(config.PublicKeys);
    private ulong[] _validatorIndices = [];
    private ulong? _keysLookedUpInEpoch;
    private CancellationTokenRegistration _driverStopped;
    private Task _run = Task.CompletedTask;

    /// <summary>Waits out a delay; tests replace it to advance a manual clock instead of sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    string IStoppableService.Description => "attester duties";

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
        if (_logger.IsInfo) _logger.Info($"Performing attester duties for {_unresolvedKeys.Length} validator keys");
        if (spec.GloasForkEpoch != Presets.FarFutureEpoch && _logger.IsWarn) _logger.Warn($"Attester duties stop at the Gloas fork epoch {spec.GloasForkEpoch}: Gloas duties are not supported yet");
        try
        {
            for (ulong slot = slotClock.CurrentSlot; ; slot = Math.Max(slot + 1, slotClock.CurrentSlot))
            {
                await PrepareAsync(slot, cancellationToken);
                long attestationDue = slotClock.SlotStartMilliseconds(slot) + _attestationDueMilliseconds;
                // Timers can fire marginally early, and attesting before the deadline is not allowed.
                for (long remaining = attestationDue - slotClock.UnixMilliseconds; remaining > 0; remaining = attestationDue - slotClock.UnixMilliseconds)
                    await Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);

                await AttestAsync(slot, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (_logger.IsInfo) _logger.Info("Attester duties stopped");
    }

    private async Task PrepareAsync(ulong slot, CancellationToken cancellationToken)
    {
        ulong epoch = spec.GetEpoch(slot);
        foreach (ulong knownEpoch in _dutiesByEpoch.Keys)
        {
            if (knownEpoch < epoch) _dutiesByEpoch.Remove(knownEpoch);
        }

        if (_unresolvedKeys.Length > 0 && _keysLookedUpInEpoch != epoch)
        {
            try
            {
                await LookUpKeysAsync(cancellationToken);
                _keysLookedUpInEpoch = epoch;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailure("Looking up the validator keys", e);
            }
        }

        if (_validatorIndices.Length == 0) return;
        // An epoch's first slot is prepared during the previous epoch, when the node still refuses the duties of the epoch after it.
        ulong lastDutyEpoch = Math.Min(epoch + 1, slotClock.CurrentEpoch + 1);
        for (ulong dutyEpoch = epoch; dutyEpoch <= lastDutyEpoch; dutyEpoch++)
        {
            // The node refuses Gloas duties; this client performs duties up to Fulu.
            if (dutyEpoch >= spec.GloasForkEpoch) break;
            try
            {
                DutiesResult<AttesterDuty> duties = await node.GetAttesterDuties(dutyEpoch, _validatorIndices, cancellationToken);
                if (_dutiesByEpoch.TryGetValue(dutyEpoch, out DutiesResult<AttesterDuty>? known) && known.DependentRoot != duties.DependentRoot && _logger.IsInfo)
                    _logger.Info($"Attester duties of epoch {dutyEpoch} changed with their dependent root, from {known.DependentRoot} to {duties.DependentRoot}");
                _dutiesByEpoch[dutyEpoch] = duties;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailure($"Fetching the attester duties of epoch {dutyEpoch}", e);
            }
        }
    }

    private async Task LookUpKeysAsync(CancellationToken cancellationToken)
    {
        HashSet<BlsPublicKey> found = [];
        foreach (ValidatorSummary validator in await node.GetValidators(_unresolvedKeys, cancellationToken))
        {
            if (!found.Add(validator.PublicKey)) continue;
            _validatorIndices = [.. _validatorIndices, validator.ValidatorIndex];
            if (_logger.IsInfo) _logger.Info($"Validator {validator.PublicKey} is index {validator.ValidatorIndex}");
        }

        _unresolvedKeys = Array.FindAll(_unresolvedKeys, key => !found.Contains(key));
        if (_unresolvedKeys.Length > 0 && _logger.IsInfo) _logger.Info($"{_unresolvedKeys.Length} validator keys are not in the registry yet; looking them up again next epoch");
    }

    private async Task AttestAsync(ulong slot, CancellationToken cancellationToken)
    {
        List<AttesterDuty> duties = [];
        if (_dutiesByEpoch.TryGetValue(spec.GetEpoch(slot), out DutiesResult<AttesterDuty>? epochDuties))
        {
            foreach (AttesterDuty duty in epochDuties.Duties)
            {
                if (duty.Slot == slot) duties.Add(duty);
            }
        }

        if (duties.Count == 0) return;
        try
        {
            if (slotClock.CurrentSlot > slot)
            {
                Skip("Late", duties.Count, $"slot {slot} passed while preparing its duties");
                return;
            }

            SyncingStatus status = await node.GetSyncingStatus(cancellationToken);
            if (status.IsSyncing || status.IsOptimistic)
            {
                Skip(status.IsSyncing ? "Syncing" : "Optimistic", duties.Count, status.IsSyncing ? $"the node is {status.SyncDistance} slots behind" : "the head is optimistic");
                return;
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
            for (int i = 0; i < signingTasks.Length; i++) signingTasks[i] = SignAsync(signing[i], data, cancellationToken);
            BlsSignature?[] signatures = await Task.WhenAll(signingTasks);

            List<SingleAttestation> attestations = [];
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i] is { } signature)
                    attestations.Add(new SingleAttestation { CommitteeIndex = signing[i].CommitteeIndex, AttesterIndex = signing[i].ValidatorIndex, Data = data, Signature = signature });
            }

            if (attestations.Count == 0) return;
            IReadOnlyList<SubmissionFailure> failures = await node.SubmitAttestations(attestations, cancellationToken);
            foreach (SubmissionFailure failure in failures)
                Skip("RefusedOnSubmit", 1, $"the node refused the attestation of validator {attestations[failure.Index].AttesterIndex}: {failure.Message}");

            Interlocked.Add(ref Metrics.ValidatorAttestationsSubmittedCount, (ulong)(attestations.Count - failures.Count));
            if (_logger.IsInfo) _logger.Info($"Submitted {attestations.Count - failures.Count} attestations for slot {slot}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Skip(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", duties.Count, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Attester duties of slot {slot} failed unexpectedly.", e);
        }
    }

    private async Task<BlsSignature?> SignAsync(AttesterDuty duty, AttestationData data, CancellationToken cancellationToken)
    {
        try
        {
            return await signer.SignAttestation(duty.PublicKey, data, cancellationToken);
        }
        catch (ValidatorSignerException e)
        {
            Interlocked.Increment(ref Metrics.ValidatorSignerFailuresCount);
            Skip(e.Refused ? "SignerRefused" : "SignerUnreachable", 1, e.Message);
            return null;
        }
    }

    private void Skip(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorAttestationsSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} attester duties: {detail}");
    }

    private void LogFailure(string action, Exception e)
    {
        if (e is BeaconNodeApiException)
        {
            if (_logger.IsWarn) _logger.Warn($"{action} failed: {e.Message}");
        }
        else if (_logger.IsError) _logger.Error($"{action} failed unexpectedly.", e);
    }

    private static BlsPublicKey[] ParsePublicKeys(string[] publicKeys)
    {
        if (publicKeys.Length == 0)
            throw new InvalidConfigurationException("ValidatorClient.PublicKeys is empty; name at least one validator key.", ExitCodes.ForbiddenOptionValue);

        BlsPublicKey[] parsed = new BlsPublicKey[publicKeys.Length];
        for (int i = 0; i < parsed.Length; i++)
        {
            if (!HexConvert.TryParsePubKey(publicKeys[i], out parsed[i]))
                throw new InvalidConfigurationException($"ValidatorClient.PublicKeys entry '{publicKeys[i]}' is not a 0x-prefixed 48-byte BLS public key.", ExitCodes.ForbiddenOptionValue);
        }

        return parsed;
    }
}
