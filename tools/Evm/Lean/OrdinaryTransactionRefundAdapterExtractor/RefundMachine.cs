// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

/// <summary>Executable fixed-width oracle for testing the refund adapter's raw-observation contract.</summary>
/// <remarks>This oracle is not itself a source-admission result or a Lean proof.</remarks>
internal static class RefundMachine
{
    private static readonly BigInteger UInt256Modulus = BigInteger.One << 256;

    internal static RefundResult Evaluate(RefundObservations input, RefundConstants constants)
    {
        RequireUInt256(input.GasPrice);
        RequireUInt256(input.MaxFeePerGas);
        RequireUInt256(input.MaxPriorityFeePerGas);
        if (input.DestroyCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "A collection count cannot be negative.");
        }
        if (constants.LegacyRefundQuotient == 0 || constants.Eip3529RefundQuotient == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(constants));
        }

        GasState gas = input.IncomingGas;
        ulong codeRefund = 0;
        if (input.Entry == RefundEntry.OrdinaryRefund)
        {
            if (input.IsEip8037Enabled && input.ShouldRevert && input.TopLevelCreateStateGasCharged)
            {
                long amount = input.IsContractCreation ? constants.CreateStateCost : 0;
                if (amount > 0)
                {
                    gas = RefundStateGas(gas, amount, input.IntrinsicStandard.StateReservoir, false);
                }
            }

            codeRefund = input.CodeInsertRefundCount == 0 || input.IsEip8037Enabled
                ? 0
                : unchecked((constants.NewAccountCost - constants.PerAuthorizationCost) * input.CodeInsertRefundCount);
            if (!input.IsError || !input.IsEip8037Enabled)
            {
                SettlementInput settlement = new(
                    input.TransactionGasLimit,
                    input.IsError ? 0 : PreRefundGas(gas, input.TransactionGasLimit),
                    input.RefundCounter,
                    input.DestroyCount,
                    input.DestroyRefund,
                    codeRefund,
                    input.FloorGas.Value,
                    input.IsEip8037Enabled ? gas.StateGasUsed : 0,
                    input.IsEip3529Enabled ? constants.Eip3529RefundQuotient : constants.LegacyRefundQuotient,
                    input.IsError,
                    input.ShouldRevert,
                    input.IsEip8037Enabled,
                    input.IsEip7778Enabled);
                return Finish(input, gas, settlement, Settle(settlement), ShouldRefund(input), null);
            }
        }
        else if (!input.IsEip8037Enabled)
        {
            if (input.Entry is not (RefundEntry.ContractCollision or RefundEntry.FailedDeposit))
            {
                throw new ArgumentException("Direct preparation and CREATE-state halt entries require EIP-8037.", nameof(input));
            }

            ulong limit = input.TransactionGasLimit;
            return Finish(input, gas, null, new(limit, limit, 0, 0, limit, 0), false, null);
        }

        long initialReservoir = Math.Max(0, unchecked(unchecked((long)input.TransactionGasLimit - input.IntrinsicStandard.StateReservoir) - (long)constants.ExecutionCap));
        long refundedIntrinsic = Math.Max(0, unchecked(input.PostIntrinsicStateReservoir - initialReservoir));
        long haltFloor = Math.Max(0, unchecked(input.IntrinsicStandard.StateReservoir - refundedIntrinsic));
        if (input.IsEip8037Enabled && gas.StateGasUsed > haltFloor)
        {
            gas = RefundStateGas(gas, gas.StateGasUsed, haltFloor, true);
        }

        gas = ResetForHalt(gas, input.PostIntrinsicStateReservoir, haltFloor);
        gas = ClearExecutionGas(gas);
        ulong before = unchecked(input.TransactionGasLimit - (ulong)gas.StateReservoir);
        ulong quotient = input.IsEip3529Enabled ? constants.Eip3529RefundQuotient : constants.LegacyRefundQuotient;
        ulong claimed = Math.Min(before / quotient, codeRefund);
        ulong spent = Math.Max(unchecked(before - claimed), input.FloorGas.Value);
        ulong stateUsed = unchecked((ulong)gas.StateGasUsed);
        ulong blockExecution = Math.Max(before >= stateUsed ? before - stateUsed : 0, input.FloorGas.Value);
        ConsumedGas consumed = new(spent, spent, blockExecution, stateUsed, spent, claimed);
        return Finish(input, gas, null, consumed, ShouldRefund(input) && spent < input.TransactionGasLimit, haltFloor);
    }

    internal static ulong PreRefundGas(GasState gas, ulong limit)
    {
        Int128 difference = (Int128)limit - gas.Value - gas.StateReservoir;
        return difference >= 0 && difference <= ulong.MaxValue ? (ulong)difference : limit;
    }

    internal static GasState RefundStateGas(GasState gas, long amount, long floor, bool trackSpillRefund)
    {
        long refundable = Math.Max(0, unchecked(gas.StateGasUsed - floor));
        long applied = Math.Min(amount, refundable);
        long toExecution = trackSpillRefund ? Math.Min(applied, Math.Max(0, unchecked(gas.StateGasSpill - gas.StateGasSpillRefunded))) : 0;
        return new(
            unchecked(gas.Value + (ulong)toExecution),
            unchecked(gas.StateReservoir + unchecked(applied - toExecution)),
            unchecked(gas.StateGasUsed - applied),
            gas.StateGasSpill,
            trackSpillRefund ? unchecked(gas.StateGasSpillRefunded + toExecution) : gas.StateGasSpillRefunded);
    }

    internal static GasState ResetForHalt(GasState gas, long reservoir, long used) =>
        gas with { StateReservoir = reservoir, StateGasUsed = used, StateGasSpill = 0 };

    internal static GasState ClearExecutionGas(GasState gas) => gas with { Value = 0 };

    internal static bool ShouldRefund(RefundObservations input) =>
        !input.GasPrice.IsZero && (!input.SkipValidation || !input.MaxFeePerGas.IsZero || !input.MaxPriorityFeePerGas.IsZero);

    internal static ConsumedGas Settle(SettlementInput input)
    {
        ulong before = input.IsError ? input.TransactionGasLimit : input.PreRefundGas;
        long total = unchecked((long)input.CodeInsertExecutionRefund);
        if (!input.IsError && !input.ShouldRevert)
        {
            total = unchecked(total + unchecked(input.RefundCounter + unchecked(input.DestroyCount * (long)input.DestroyRefund)));
        }

        long refund = Math.Min(unchecked((long)(before / input.RefundQuotient)), total);
        ulong operation = refund >= 0 ? unchecked(before - (ulong)refund) : unchecked(before + (ulong)unchecked(-refund));
        ulong spent = Math.Max(operation, input.CalldataFloorGas);
        ulong blockState = input.IsEip8037Enabled ? unchecked((ulong)input.StateGasUsed) : 0;
        ulong blockGas = input.IsEip8037Enabled
            ? Math.Max(before >= blockState ? before - blockState : 0, input.CalldataFloorGas)
            : input.IsEip7778Enabled ? Math.Max(before, input.CalldataFloorGas) : 0;
        return new(spent, operation, blockGas, blockState, Math.Max(before, input.CalldataFloorGas), refund > 0 ? (ulong)refund : 0);
    }

    private static RefundResult Finish(RefundObservations input, GasState gas, SettlementInput? settlement, ConsumedGas consumed, bool call, long? haltFloor)
    {
        BigInteger amount = call ? unchecked(input.TransactionGasLimit - consumed.SpentGas) * input.GasPrice % UInt256Modulus : BigInteger.Zero;
        bool modifiesCaller = input.Entry is RefundEntry.PreparationOutOfGas or RefundEntry.CreateStateOutOfGas ||
            input.Entry == RefundEntry.FailedDeposit && input.IsEip8037Enabled;
        return new(gas, modifiesCaller ? gas : input.IncomingGas, settlement, consumed, call, amount, call && !amount.IsZero ? amount : null, haltFloor);
    }

    private static void RequireUInt256(BigInteger value)
    {
        if (value < 0 || value >= UInt256Modulus)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The raw UInt256 observation is outside its machine domain.");
        }
    }
}
