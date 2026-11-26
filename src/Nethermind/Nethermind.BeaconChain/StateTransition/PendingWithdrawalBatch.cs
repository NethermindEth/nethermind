// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Tracks sequential pending-withdrawal changes within one operations batch.</summary>
internal sealed class PendingWithdrawalBatch(PendingPartialWithdrawal[] pending)
{
    private Dictionary<ulong, ulong>? _totals;
    private int? _firstIndex;
    private ulong _firstTotal;
    private readonly List<PendingPartialWithdrawal> _added = [];

    public int Count => pending.Length + _added.Count;

    public ulong GetTotal(int validatorIndex)
    {
        if (_totals is null)
        {
            if (_firstIndex is null)
            {
                _firstIndex = validatorIndex;
                foreach (PendingPartialWithdrawal withdrawal in pending)
                    if (withdrawal.ValidatorIndex == (ulong)validatorIndex) _firstTotal += withdrawal.Amount;
                foreach (PendingPartialWithdrawal withdrawal in _added)
                    if (withdrawal.ValidatorIndex == (ulong)validatorIndex) _firstTotal += withdrawal.Amount;
                return _firstTotal;
            }
            if (_firstIndex == validatorIndex) return _firstTotal;

            _totals = [];
            foreach (PendingPartialWithdrawal withdrawal in pending)
                AddTotal(withdrawal);
            foreach (PendingPartialWithdrawal withdrawal in _added)
                AddTotal(withdrawal);
        }
        return _totals.GetValueOrDefault((ulong)validatorIndex);
    }

    public void Add(PendingPartialWithdrawal withdrawal)
    {
        _added.Add(withdrawal);
        if (_totals is not null)
            AddTotal(withdrawal);
        else if (_firstIndex is int first && withdrawal.ValidatorIndex == (ulong)first)
            _firstTotal += withdrawal.Amount;
    }

    private void AddTotal(PendingPartialWithdrawal withdrawal) =>
        CollectionsMarshal.GetValueRefOrAddDefault(_totals!, withdrawal.ValidatorIndex, out _) += withdrawal.Amount;

    public PendingPartialWithdrawal[] ToArray() => _added.Count == 0 ? pending : [.. pending, .. _added];
}
