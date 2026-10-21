// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.LightClient.Consensus;

internal static class ConsensusSync
{
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
