// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Blockchain.Tracing;

internal readonly struct BlockReceiptGasAccountingResult(
    ulong cumulativeExecutionGas,
    ulong cumulativeStateGas,
    ulong cumulativeReceiptGas,
    ulong headerGasUsed)
{
    public readonly ulong CumulativeExecutionGas = cumulativeExecutionGas;
    public readonly ulong CumulativeStateGas = cumulativeStateGas;
    public readonly ulong CumulativeReceiptGas = cumulativeReceiptGas;
    public readonly ulong HeaderGasUsed = headerGasUsed;
}

internal static class BlockReceiptGasAccountingKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlockReceiptGasAccountingResult Accumulate(
        ulong previousExecutionGas,
        ulong previousStateGas,
        ulong previousReceiptGas,
        ulong transactionExecutionGas,
        ulong transactionStateGas,
        ulong transactionPaidGas)
    {
        ulong cumulativeExecutionGas = unchecked(previousExecutionGas + transactionExecutionGas);
        ulong cumulativeStateGas = unchecked(previousStateGas + transactionStateGas);
        ulong cumulativeReceiptGas = unchecked(previousReceiptGas + transactionPaidGas);
        return FromTotals(cumulativeExecutionGas, cumulativeStateGas, cumulativeReceiptGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlockReceiptGasAccountingResult FromTotals(
        ulong cumulativeExecutionGas,
        ulong cumulativeStateGas,
        ulong cumulativeReceiptGas) =>
        new(
            cumulativeExecutionGas,
            cumulativeStateGas,
            cumulativeReceiptGas,
            EthereumGasPolicy.CombineBlockGas(cumulativeExecutionGas, cumulativeStateGas));
}
