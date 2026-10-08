// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.LightClient.Consensus;

internal static class ConsensusSync
{
    internal static async Task CatchUpAsync(LightClientStore store, BeaconChainSpec spec,
        Func<ulong, Action<LightClientUpdate>, CancellationToken, Task<LightClientUpdate>> fetchUpdate, Func<ulong> currentSlot,
        Func<LightClientUpdate, CancellationToken, Task> persistValidatedUpdate,
        Func<CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ulong currentPeriod = spec.GetEpoch(currentSlot()) / Presets.EpochsPerSyncCommitteePeriod;
        for (int i = 0; i < 4 && (store.Period < currentPeriod || !store.NextSyncCommitteeKnown); i++)
        {
            ulong before = store.Period;
            bool knewNext = store.NextSyncCommitteeKnown;
            ulong requestedPeriod = before + (knewNext ? 1UL : 0UL);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            LightClientUpdate update = await fetchUpdate(requestedPeriod,
                value => store.Process(value, currentSlot()), timeout.Token);
            await persistValidatedUpdate(update, cancellationToken);
            await publish(cancellationToken);
            if (store.Period == before && store.NextSyncCommitteeKnown == knewNext) break;
        }
    }
}
