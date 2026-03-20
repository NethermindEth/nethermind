// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Represents gas consumption information for a transaction.
/// </summary>
/// <param name="SpentGas">Gas after refunds (what user pays).</param>
/// <param name="OperationGas">Gas used for EVM operations.</param>
/// <param name="BlockGas">EIP-7778: Regular gas for block accounting (pre-refund). When 0, use SpentGas.</param>
/// <param name="BlockStateGas">EIP-8037: State gas for block accounting. Block gasUsed = max(sum_regular, sum_state).</param>
/// <param name="MaxUsedGas">Maximum gas consumed before refunds; if 0, use SpentGas.</param>
public readonly record struct GasConsumed(long SpentGas, long OperationGas, long BlockGas = 0, long BlockStateGas = 0, long MaxUsedGas = 0)
{
    /// <summary>
    /// Gets the effective regular gas for block accounting. When EIP-7778 is enabled,
    /// this returns BlockGas (pre-refund), otherwise returns SpentGas.
    /// </summary>
    public long EffectiveBlockGas => BlockGas > 0 ? BlockGas : SpentGas;

    /// <summary>
    /// Gets gas consumed before refunds (and floor adjusted), used by eth_simulate maxUsedGas.
    /// </summary>
    public long EffectiveMaxUsedGas => MaxUsedGas > 0 ? MaxUsedGas : SpentGas;

    public static implicit operator long(GasConsumed gas) => gas.SpentGas;
    public static implicit operator GasConsumed(long spentGas) => new(spentGas, spentGas, 0, 0, spentGas);
}
