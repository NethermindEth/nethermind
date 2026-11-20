// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Indexes settled pending balances for one ordered builder-exit batch.</summary>
internal sealed class BuilderExitBalances(BeaconStateGloas state, BuilderRegistryBatch builders, BuilderExitRequest[] requests)
{
    private Dictionary<ulong, ulong>? _totals;
    private int? _firstIndex;
    private ulong _firstTotal;

    public ulong GetTotal(int builderIndex)
    {
        if (_totals is null)
        {
            if (_firstIndex is null)
            {
                _firstIndex = builderIndex;
                return _firstTotal = state.GetPendingBalanceToWithdrawForBuilder((ulong)builderIndex);
            }
            if (_firstIndex == builderIndex) return _firstTotal;

            _totals = [with(requests.Length)];
            foreach (BuilderExitRequest request in requests)
            {
                int index = builders.Find(request.Pubkey);
                if (index >= 0) _totals.TryAdd((ulong)index, 0);
            }
            foreach (BuilderPendingWithdrawal withdrawal in state.BuilderPendingWithdrawals ?? [])
                Add(withdrawal);
            foreach (BuilderPendingPayment payment in state.BuilderPendingPayments ?? [])
                Add(payment.Withdrawal!);
        }
        return _totals.GetValueOrDefault((ulong)builderIndex);
    }

    private void Add(BuilderPendingWithdrawal withdrawal)
    {
        if (_totals!.TryGetValue(withdrawal.BuilderIndex, out ulong total))
            _totals[withdrawal.BuilderIndex] = total + withdrawal.Amount;
    }
}
