// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

/// <summary>Whether an account-access decision consumes execution gas.</summary>
internal enum AccountAccessPricingDecision : byte
{
    NoCharge,
    Charge,
}

/// <summary>Pure account-access pricing result returned by the EIP-8038 policy kernel.</summary>
internal readonly struct AccountAccessPricingResult(
    AccountAccessPricingDecision decision,
    ulong amount)
{
    public readonly AccountAccessPricingDecision Decision = decision;
    public readonly ulong Amount = amount;
}

/// <summary>Allocation-free value kernel for hot/cold account-access charging.</summary>
/// <remarks>
/// The caller supplies all fork, access, precompile, and schedule facts. This kernel does not
/// read the access tracker, release specification, world state, or gas state. SELFDESTRUCT keeps
/// its legacy warm-beneficiary exception before EIP-8038; EIP-8038 charges the supplied warm cost.
/// </remarks>
internal static class AccountAccessPricingKernel
{
    /// <summary>Chooses the account-access charge from already gathered policy facts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AccountAccessPricingResult Price(
        bool hotAndColdEnabled,
        bool eip8038Enabled,
        bool isCold,
        bool isPrecompile,
        AccountAccessKind kind,
        ulong coldAccountAccessGas,
        ulong warmAccessGas)
    {
        if (!hotAndColdEnabled)
            return new AccountAccessPricingResult(AccountAccessPricingDecision.NoCharge, 0);

        if (isCold && !isPrecompile)
            return new AccountAccessPricingResult(AccountAccessPricingDecision.Charge, coldAccountAccessGas);

        if (kind == AccountAccessKind.SelfDestructBeneficiary && !eip8038Enabled)
            return new AccountAccessPricingResult(AccountAccessPricingDecision.NoCharge, 0);

        return new AccountAccessPricingResult(AccountAccessPricingDecision.Charge, warmAccessGas);
    }
}
