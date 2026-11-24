// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Sync;
using Nethermind.Core;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>The fee recipient each validator client prepared for its validators (beacon-APIs <c>prepareBeaconProposer</c>), which the node builds their payloads with.</summary>
/// <remarks>
/// <para>
/// A preparation holds through the epoch it was made in and the <see cref="RetainedEpochs"/> after it, as the route describes; validator
/// clients send it again every epoch. At most <see cref="MaxPreparations"/> validators are held: a batch that would pass it is refused whole.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class ProposerPreparations(SlotClock slotClock)
{
    /// <summary>The epochs after the one a preparation was made in that it still holds for.</summary>
    internal const ulong RetainedEpochs = 2;

    /// <summary>The most validators held at once.</summary>
    internal const int MaxPreparations = 1 << 16;

    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, (Address FeeRecipient, ulong LastEpoch)> _byValidator = [];
    private ulong _prunedAtEpoch;

    /// <summary>Whether no validator is prepared, so no payload has to be built early.</summary>
    internal bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                Prune(slotClock.CurrentEpoch);
                return _byValidator.Count == 0;
            }
        }
    }

    /// <summary>Records the fee recipient of each validator, replacing an earlier one.</summary>
    /// <returns><c>false</c> when the batch would hold more than <see cref="MaxPreparations"/> validators; then nothing is recorded.</returns>
    internal bool TryPrepare(IReadOnlyList<ProposerPreparation> preparations)
    {
        ulong epoch = slotClock.CurrentEpoch;
        lock (_lock)
        {
            Prune(epoch);
            int added = 0;
            foreach (ProposerPreparation preparation in preparations)
            {
                if (!_byValidator.ContainsKey(preparation.ValidatorIndex)) added++;
            }

            if (_byValidator.Count + added > MaxPreparations) return false;
            foreach (ProposerPreparation preparation in preparations)
                _byValidator[preparation.ValidatorIndex] = (preparation.FeeRecipient, epoch + RetainedEpochs);
            return true;
        }
    }

    /// <summary>The fee recipient prepared for <paramref name="validatorIndex"/>, or <c>null</c> when none holds.</summary>
    internal Address? GetFeeRecipient(ulong validatorIndex)
    {
        lock (_lock)
        {
            Prune(slotClock.CurrentEpoch);
            return _byValidator.TryGetValue(validatorIndex, out (Address FeeRecipient, ulong LastEpoch) prepared) ? prepared.FeeRecipient : null;
        }
    }

    // Callers hold _lock.
    private void Prune(ulong epoch)
    {
        if (epoch == _prunedAtEpoch) return;
        _prunedAtEpoch = epoch;
        foreach ((ulong validatorIndex, (Address _, ulong lastEpoch)) in _byValidator)
        {
            if (lastEpoch < epoch) _byValidator.Remove(validatorIndex);
        }
    }
}
