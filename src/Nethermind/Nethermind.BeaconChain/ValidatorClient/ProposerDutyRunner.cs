// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Performs the block proposals of the configured validators, talking to the node only through <see cref="IBeaconNodeValidatorApi"/>.</summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="AttesterDutyRunner"/> with its own loop, so a slow block never delays an attestation. Before each slot it looks up
/// keys not yet in the registry (once an epoch), polls the proposer duties of the slot's epoch and the next, and once an epoch tells the node
/// the fee recipient of every validator found (<see cref="IValidatorClientConfig.FeeRecipient"/>). A fetch or preparation that fails is tried
/// again before the next slot.
/// </para>
/// <para>
/// At the start of a slot one of its validators proposes, it checks the node is synced, has the validator sign the randao reveal, asks the node
/// for the block and checks it is that validator's block of the slot paying the configured fee recipient, has the validator sign the block's
/// header, and publishes the block.
/// </para>
/// <para>
/// A validator is never asked to sign a block for a slot at or before one it was already asked to sign a block for. That guard lives in memory
/// only and is lost on restart; the remote signer's slashing protection is the durable one.
/// </para>
/// <para>
/// Constructing one throws <see cref="InvalidConfigurationException"/> when <see cref="IValidatorClientConfig.FeeRecipient"/> is not set to an
/// address. A proposal that fails, for whatever reason, is skipped, logged and counted; the next slot runs regardless. Gloas slots are not proposed.
/// </para>
/// </remarks>
public sealed class ProposerDutyRunner(
    IValidatorClientConfig config,
    IBeaconNodeValidatorApi node,
    IValidatorSigner signer,
    BeaconChainSpec spec,
    SlotClock slotClock,
    ILogManager logManager) : IStoppableService, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<ProposerDutyRunner>();
    private readonly ValidatorIndexLookup _validators = new(config, node, logManager.GetClassLogger<ProposerDutyRunner>());
    private readonly Address _feeRecipient = ParseFeeRecipient(config.FeeRecipient);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, IReadOnlyList<ProposerDuty>> _dutiesByEpoch = [];
    private readonly Dictionary<ulong, ulong> _lastSignedSlots = [];
    private ulong? _preparedEpoch;
    private CancellationTokenRegistration _driverStopped;
    private Task _run = Task.CompletedTask;

    /// <summary>Waits out a delay; tests replace it to advance a manual clock instead of sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    string IStoppableService.Description => "block proposal duties";

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
        if (_logger.IsInfo) _logger.Info($"Performing block proposal duties for {_validators.KeyCount} validator keys, paying fees to {_feeRecipient}");
        try
        {
            for (ulong slot = slotClock.CurrentSlot; ; slot = Math.Max(slot + 1, slotClock.CurrentSlot))
            {
                await PrepareAsync(slot, cancellationToken);
                await WaitUntil(slotClock.SlotStartMilliseconds(slot), cancellationToken);
                if (DutyAt(slot) is { } duty) await ProposeAsync(slot, duty, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (_logger.IsInfo) _logger.Info("Block proposal duties stopped");
    }

    private async Task WaitUntil(long unixMilliseconds, CancellationToken cancellationToken)
    {
        // Timers can fire marginally early, and a duty must not be performed before its slot.
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

        await _validators.Update(epoch, cancellationToken);
        if (_validators.Indices.Length == 0) return;
        HashSet<ulong> ours = [.. _validators.Indices];
        // An epoch's first slot is prepared during the previous epoch, when the node still refuses the duties of the epoch after it.
        ulong lastDutyEpoch = Math.Min(epoch + 1, slotClock.CurrentEpoch + 1);
        for (ulong dutyEpoch = epoch; dutyEpoch <= lastDutyEpoch; dutyEpoch++)
        {
            // The node refuses Gloas duties; this client performs duties up to Fulu.
            if (dutyEpoch >= spec.GloasForkEpoch) break;
            try
            {
                List<ProposerDuty> duties = [];
                foreach (ProposerDuty duty in (await node.GetProposerDuties(dutyEpoch, cancellationToken)).Duties)
                {
                    if (ours.Contains(duty.ValidatorIndex)) duties.Add(duty);
                }

                _dutiesByEpoch[dutyEpoch] = duties;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                DutyCalls.LogFailure(_logger, $"Fetching the proposer duties of epoch {dutyEpoch}", e);
            }
        }

        if (_preparedEpoch == epoch) return;
        ProposerPreparation[] preparations = new ProposerPreparation[_validators.Indices.Length];
        for (int i = 0; i < preparations.Length; i++) preparations[i] = new ProposerPreparation(_validators.Indices[i], _feeRecipient);
        try
        {
            await node.PrepareBeaconProposers(preparations, cancellationToken);
            _preparedEpoch = epoch;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DutyCalls.LogFailure(_logger, "Preparing the fee recipient of the proposers", e);
        }
    }

    private ProposerDuty? DutyAt(ulong slot)
    {
        if (!_dutiesByEpoch.TryGetValue(spec.GetEpoch(slot), out IReadOnlyList<ProposerDuty>? duties)) return null;
        foreach (ProposerDuty duty in duties)
        {
            if (duty.Slot == slot) return duty;
        }

        return null;
    }

    /// <summary>Has the proposer of <paramref name="slot"/> sign the node's block of it and publishes the block (phase0/validator.md "Block proposal").</summary>
    private async Task ProposeAsync(ulong slot, ProposerDuty duty, CancellationToken cancellationToken)
    {
        try
        {
            if (await DutyCalls.RefusalToPerform(node, slotClock, slot, cancellationToken) is { } refusal)
            {
                Skip(refusal.Reason, 1, refusal.Detail);
                return;
            }

            if (_lastSignedSlots.TryGetValue(duty.ValidatorIndex, out ulong signedSlot) && signedSlot >= slot)
            {
                Skip("AlreadySigned", 1, $"validator {duty.ValidatorIndex} was already asked to sign a block for slot {signedSlot}");
                return;
            }

            ulong epoch = spec.GetEpoch(slot);
            if (await DutyCalls.SignAsync(() => signer.SignRandaoReveal(duty.PublicKey, epoch, cancellationToken), Skip) is not { } randaoReveal) return;
            ProducedBlock produced = await node.ProduceBlock(slot, randaoReveal, Hash256.Zero, cancellationToken);
            BeaconBlock block = produced.Block;
            if (block.Slot != slot || block.ProposerIndex != duty.ValidatorIndex || block.Body?.ExecutionPayload?.FeeRecipient != _feeRecipient)
            {
                Skip("UnexpectedBlock", 1, $"the node's block for slot {slot} is of slot {block.Slot} and proposer {block.ProposerIndex}, paying {block.Body?.ExecutionPayload?.FeeRecipient}");
                return;
            }

            // Recorded before asking: a request that fails in transit may still have been signed.
            _lastSignedSlots[duty.ValidatorIndex] = slot;
            BeaconBlockHeader header = new() { Slot = block.Slot, ProposerIndex = block.ProposerIndex, ParentRoot = block.ParentRoot, StateRoot = block.StateRoot, BodyRoot = SszRoots.HashTreeRoot(block.Body) };
            if (await DutyCalls.SignAsync(() => signer.SignBlock(duty.PublicKey, header, cancellationToken), Skip) is not { } signature) return;

            await node.PublishBlock(new SignedBlockContents(new SignedBeaconBlock { Message = block, Signature = signature }, produced.Blobs, produced.KzgProofs), cancellationToken);
            Interlocked.Increment(ref Metrics.ValidatorBlocksProposedCount);
            if (_logger.IsInfo) _logger.Info($"Proposed block {SszRoots.HashTreeRoot(block)} of slot {slot} for validator {duty.ValidatorIndex} with {produced.Blobs.Count} blobs");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Skip(e is BeaconNodeApiException nodeRefusal ? nodeRefusal.Error.ToString() : "Failed", 1, e.Message);
            if (e is not BeaconNodeApiException && _logger.IsError) _logger.Error($"Block proposal of slot {slot} failed unexpectedly.", e);
        }
    }

    private void Skip(string reason, int dutyCount, string detail)
    {
        Metrics.BeaconChainValidatorProposalsSkipped.AddOrUpdate(new StringLabel(reason), dutyCount, (_, skipped) => skipped + dutyCount);
        if (_logger.IsWarn) _logger.Warn($"Skipped {dutyCount} block proposal duties: {detail}");
    }

    private static Address ParseFeeRecipient(string? feeRecipient) =>
        HexConvert.TryParseAddress(feeRecipient, out Address? address)
            ? address
            : throw new InvalidConfigurationException($"ValidatorClient.FeeRecipient '{feeRecipient}' is not a 0x-prefixed 20-byte execution address; it is required to propose blocks.", ExitCodes.ForbiddenOptionValue);
}
