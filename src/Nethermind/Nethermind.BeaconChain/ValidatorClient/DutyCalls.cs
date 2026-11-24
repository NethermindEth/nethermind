// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>What every duty runner does around its calls to the node and the signer.</summary>
internal static class DutyCalls
{
    /// <summary>Why the duties of <paramref name="slot"/> must not be performed now, or <c>null</c> when they may be.</summary>
    public static async Task<(string Reason, string Detail)?> RefusalToPerform(IBeaconNodeValidatorApi node, SlotClock slotClock, ulong slot, CancellationToken cancellationToken)
    {
        if (slotClock.CurrentSlot > slot) return ("Late", $"slot {slot} passed while preparing its duties");
        SyncingStatus status = await node.GetSyncingStatus(cancellationToken);
        return status.IsSyncing ? ("Syncing", $"the node is {status.SyncDistance} slots behind")
            : status.IsOptimistic ? ("Optimistic", "the head is optimistic")
            : null;
    }

    /// <returns>The signature, or <c>null</c> when the signer did not sign, which is counted and passed to <paramref name="skip"/> with its reason.</returns>
    public static async Task<BlsSignature?> SignAsync(Func<ValueTask<BlsSignature>> sign, Action<string, int, string> skip)
    {
        try
        {
            return await sign();
        }
        catch (ValidatorSignerException e)
        {
            Interlocked.Increment(ref Metrics.ValidatorSignerFailuresCount);
            skip(e.Refused ? "SignerRefused" : "SignerUnreachable", 1, e.Message);
            return null;
        }
    }

    /// <summary>Logs a failed call to the node: a refusal as a warning, anything else as an error with its stack.</summary>
    public static void LogFailure(ILogger logger, string action, Exception e)
    {
        if (e is BeaconNodeApiException)
        {
            if (logger.IsWarn) logger.Warn($"{action} failed: {e.Message}");
        }
        else if (logger.IsError) logger.Error($"{action} failed unexpectedly.", e);
    }
}
