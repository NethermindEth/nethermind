// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Discriminator for account access gas charging.
/// Allows <see cref="IGasPolicy{TSelf}.ConsumeAccountAccessGas"/> to vary
/// its resource-kind split based on the calling opcode's semantics.
/// </summary>
public enum AccountAccessKind : byte
{
    /// <summary>
    /// Regular account access (BALANCE, EXTCODESIZE, CALL, etc.).
    /// </summary>
    Default = 0,

    /// <summary>
    /// SELFDESTRUCT beneficiary access.
    /// Cold access charges full cost to StorageAccess (no Computation split);
    /// warm access charges nothing.
    /// </summary>
    SelfDestructBeneficiary = 1
}
