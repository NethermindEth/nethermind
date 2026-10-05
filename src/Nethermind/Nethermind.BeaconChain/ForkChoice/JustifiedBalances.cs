// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Effective balances of the validators in a justified state, with precomputed aggregates.</summary>
/// <param name="effectiveBalances">Per-validator effective balances in Gwei; inactive or slashed validators must be reported as zero.</param>
/// <param name="totalEffectiveBalance">The committee-weight base: the justified state's <c>get_total_active_balance</c>, which counts slashed validators and is at least <c>EFFECTIVE_BALANCE_INCREMENT</c>; <see cref="FromEffectiveBalances"/> uses the plain sum.</param>
public sealed class JustifiedBalances(IReadOnlyList<ulong> effectiveBalances, ulong totalEffectiveBalance)
{
    public static readonly JustifiedBalances Empty = new([], 0);

    public IReadOnlyList<ulong> EffectiveBalances { get; } = effectiveBalances;

    public ulong TotalEffectiveBalance { get; } = totalEffectiveBalance;

    public static JustifiedBalances FromEffectiveBalances(IReadOnlyList<ulong> effectiveBalances)
    {
        ulong total = 0;
        for (int i = 0; i < effectiveBalances.Count; i++)
        {
            total = checked(total + effectiveBalances[i]);
        }

        return new JustifiedBalances(effectiveBalances, total);
    }
}
