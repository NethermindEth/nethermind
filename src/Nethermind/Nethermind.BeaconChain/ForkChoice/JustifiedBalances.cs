// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Effective balances of the validators in a justified state, with precomputed aggregates.</summary>
/// <param name="effectiveBalances">Per-validator effective balances in Gwei; inactive or slashed validators must be reported as zero.</param>
/// <param name="totalEffectiveBalance">The sum of <paramref name="effectiveBalances"/>.</param>
/// <param name="numActiveValidators">The number of non-zero entries in <paramref name="effectiveBalances"/>.</param>
public sealed class JustifiedBalances(IReadOnlyList<ulong> effectiveBalances, ulong totalEffectiveBalance, ulong numActiveValidators)
{
    public static readonly JustifiedBalances Empty = new([], 0, 0);

    public IReadOnlyList<ulong> EffectiveBalances { get; } = effectiveBalances;

    public ulong TotalEffectiveBalance { get; } = totalEffectiveBalance;

    public ulong NumActiveValidators { get; } = numActiveValidators;

    public static JustifiedBalances FromEffectiveBalances(IReadOnlyList<ulong> effectiveBalances)
    {
        ulong total = 0;
        ulong active = 0;
        for (int i = 0; i < effectiveBalances.Count; i++)
        {
            ulong balance = effectiveBalances[i];
            if (balance != 0)
            {
                total = checked(total + balance);
                active++;
            }
        }

        return new JustifiedBalances(effectiveBalances, total, active);
    }
}
