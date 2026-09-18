// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm.TransactionProcessing;

internal readonly struct TransactionSettlementResult(
    ulong spentGas,
    ulong operationGas,
    ulong blockGas,
    ulong blockStateGas,
    ulong maxUsedGas,
    ulong gasRefund)
{
    public readonly ulong SpentGas = spentGas;
    public readonly ulong OperationGas = operationGas;
    public readonly ulong BlockGas = blockGas;
    public readonly ulong BlockStateGas = blockStateGas;
    public readonly ulong MaxUsedGas = maxUsedGas;
    public readonly ulong GasRefund = gasRefund;
}

internal static class TransactionSettlementKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionSettlementResult Calculate(
        ulong transactionGasLimit,
        ulong preRefundGas,
        long refundCounter,
        int destroyCount,
        ulong destroyRefund,
        ulong codeInsertExecutionRefund,
        ulong calldataFloorGas,
        long stateGasUsed,
        ulong refundQuotient,
        bool isError,
        bool shouldRevert,
        bool isEip8037Enabled,
        bool isEip7778Enabled)
    {
        ulong gasUsedBeforeRefund = isError ? transactionGasLimit : preRefundGas;
        long totalToRefund = unchecked((long)codeInsertExecutionRefund);

        if (!isError && !shouldRevert)
        {
            long destroyRefundTotal = unchecked(destroyCount * (long)destroyRefund);
            totalToRefund = unchecked(totalToRefund + unchecked(refundCounter + destroyRefundTotal));
        }

        long refund = Math.Min(unchecked((long)(gasUsedBeforeRefund / refundQuotient)), totalToRefund);
        ulong operationGas = refund >= 0
            ? unchecked(gasUsedBeforeRefund - (ulong)refund)
            : unchecked(gasUsedBeforeRefund + (ulong)unchecked(-refund));
        ulong spentGas = Math.Max(operationGas, calldataFloorGas);

        ulong blockGas;
        ulong blockStateGas;
        if (isEip8037Enabled)
        {
            blockStateGas = unchecked((ulong)stateGasUsed);
            blockGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(
                gasUsedBeforeRefund,
                blockStateGas,
                calldataFloorGas);
        }
        else
        {
            blockStateGas = 0;
            blockGas = isEip7778Enabled ? Math.Max(gasUsedBeforeRefund, calldataFloorGas) : 0;
        }

        return new TransactionSettlementResult(
            spentGas,
            operationGas,
            blockGas,
            blockStateGas,
            Math.Max(gasUsedBeforeRefund, calldataFloorGas),
            refund > 0 ? (ulong)refund : 0);
    }
}
