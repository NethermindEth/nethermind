// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Consensus;

internal static class ConsensusSync
{
    private const ulong RepairHeadMaxAgeSlots = 2;
    private static readonly TimeSpan HealthyRepairInterval = TimeSpan.FromMinutes(1);

    internal static async Task<(LightClientStore Store, bool Resumed)> BootstrapAsync(
        BeaconChainSpec spec, Hash256 checkpoint, VerifiedConsensusJournal journal,
        Func<Hash256, Func<LightClientBootstrap, CancellationToken, Task>, CancellationToken, Task<LightClientBootstrap>> fetchBootstrap,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong CurrentSlot() => spec.GetSlotAtTime((ulong)clock.GetUtcNow().ToUnixTimeSeconds());
        LightClientStore? resumed = await journal.LoadAsync(CurrentSlot(), cancellationToken);
        if (resumed is not null) return (resumed, true);

        LightClientStore? acceptedStore = null;
        LightClientBootstrap bootstrap = await fetchBootstrap(checkpoint, (value, validationToken) =>
        {
            validationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            // Unproved peer data must not cause a terminal local clock failure.
            LightClientStore candidate = new(spec, checkpoint, value, value.Header?.Beacon?.Slot ?? CurrentSlot());
            try
            {
                ulong currentSlot = CurrentSlot();
                if (candidate.FinalizedHeader.Beacon!.Slot > currentSlot)
                    throw new LightClientLocalStateException("Checkpoint is in the future.");
                candidate.RequireRecent(currentSlot);
                validationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (LightClientLocalStateException exception)
            {
                validationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The authenticated trusted checkpoint cannot initialize the light client; verify the system clock or supply a recent trusted checkpoint.", exception);
            }
            acceptedStore = candidate;
            return Task.CompletedTask;
        }, cancellationToken);
        LightClientStore store = acceptedStore ?? throw new InvalidDataException("Beacon peers did not supply a valid trusted checkpoint.");
        await journal.InitializeAsync(bootstrap, store, cancellationToken);
        return (store, false);
    }

    internal static async Task RunRepairLoopsAsync(Func<CancellationToken, Task> repair,
        Func<CancellationToken, Task> pollOptimistic, Func<ulong> getOptimisticSlot, Func<ulong> currentSlot,
        TimeSpan slotDuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] workers =
        [
            RunRepairLoopAsync(repair, getOptimisticSlot, currentSlot, slotDuration, clock, stop.Token),
            RunRepairLoopAsync(pollOptimistic, getOptimisticSlot, currentSlot, slotDuration, clock, stop.Token),
        ];
        try { await Task.WhenAny(workers); }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers);
        }
    }

    internal static async Task RunRepairLoopAsync(Func<CancellationToken, Task> repair,
        Func<ulong> getOptimisticSlot, Func<ulong> currentSlot, TimeSpan slotDuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await repair(cancellationToken);
            long repairedAt = clock.GetTimestamp();
            while (true)
            {
                TimeSpan remaining = HealthyRepairInterval - clock.GetElapsedTime(repairedAt);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(slotDuration < remaining ? slotDuration : remaining, clock, cancellationToken);
                ulong optimisticSlot = getOptimisticSlot();
                ulong now = currentSlot();
                if (now < optimisticSlot || now - optimisticSlot > RepairHeadMaxAgeSlots) break;
            }
        }
    }

    internal static async Task CatchUpAsync(LightClientStore store, BeaconChainSpec spec,
        Func<ulong, Func<LightClientUpdate, CancellationToken, Task>, CancellationToken, Task<LightClientUpdate>> fetchUpdate, Func<ulong> currentSlot,
        Func<LightClientUpdate, CancellationToken, Task> processUpdate,
        CancellationToken cancellationToken)
    {
        ulong currentPeriod = spec.GetEpoch(currentSlot()) / Presets.EpochsPerSyncCommitteePeriod;
        for (int i = 0; i < 4; i++)
        {
            (ulong before, bool knewNext) = store.SyncProgress;
            if (before >= currentPeriod && knewNext) break;
            ulong requestedPeriod = before + (knewNext ? 1UL : 0UL);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await fetchUpdate(requestedPeriod, processUpdate, timeout.Token);
            if (store.SyncProgress == (before, knewNext)) break;
        }
    }
}
