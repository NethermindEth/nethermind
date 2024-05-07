// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;

internal sealed partial class EvmInstructions
{

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        gasAvailable -= spec.GetBalanceCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) return EvmExceptionType.OutOfGas;

        UInt256 result = vmState.WorldState.GetBalance(address);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.ExtCodeHashOpcodeEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) return EvmExceptionType.OutOfGas;

        var state = vmState.WorldState;
        if (!state.AccountExists(address) || state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            stack.PushBytes(state.GetCodeHash(address).Bytes);
        }

        return EvmExceptionType.None;
    }


    [SkipLocalsInit]
    public static EvmExceptionType InstructionMLoad(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) return EvmExceptionType.OutOfGas;
        Span<byte> bytes = vmState.Memory.LoadSpan(in result);
        if (vmState.TxTracer.IsTracingInstructions) vmState.TxTracer.ReportMemoryChange(result, bytes);

        stack.PushBytes(bytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        Span<byte> bytes = stack.PopWord256();
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) return EvmExceptionType.OutOfGas;
        vmState.Memory.SaveWord(in result, bytes);
        if (vmState.TxTracer.IsTracingInstructions) vmState.TxTracer.ReportMemoryChange((long)result, bytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore8(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        byte data = stack.PopByte();
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in UInt256.One)) return EvmExceptionType.OutOfGas;
        vmState.Memory.SaveByte(in result, data);
        if (vmState.TxTracer.IsTracingInstructions) vmState.TxTracer.ReportMemoryChange((long)result, data);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.SelfBalanceOpcodeEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.SelfBalance;

        UInt256 result = vmState.WorldState.GetBalance(vmState.Env.ExecutingAccount);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    public static bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true)
    {
        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (vmState.TxTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.WarmUp(address);
            }

            if (vmState.IsCold(address) && !address.IsPrecompile(spec))
            {
                result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                vmState.WarmUp(address);
            }
            else if (chargeForWarm)
            {
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }

    public interface IOpEnvBytes
    {
        virtual static long GasCost => GasCostOf.Base;
        abstract static void Operation(EvmState vmState, out Span<byte> result);
    }
    public interface IOpEnvUInt256
    {
        virtual static long GasCost => GasCostOf.Base;
        abstract static void Operation(EvmState vmState, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvBytes<TOpEnv>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TOpEnv : struct, IOpEnvBytes
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vmState, out Span<byte> result);

        stack.PushBytes(result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt256<TOpEnv>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TOpEnv : struct, IOpEnvUInt256
    {
        if (typeof(TOpEnv) == typeof(OpBaseFee) && !vmState.Spec.BaseFeeEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vmState, out UInt256 result);

        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    public struct OpAddress : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.ExecutingAccount.Bytes;
    }

    public struct OpCaller : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.Caller.Bytes;
    }

    public struct OpCallValue : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.Value;
    }

    public struct OpOrigin : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.Origin.Bytes;
    }

    public struct OpCallDataSize : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.InputData.Length;
    }

    public struct OpCodeSize : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.CodeInfo.MachineCode.Length;
    }

    public struct OpGasPrice : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.GasPrice;
    }

    public struct OpCoinbase : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasBeneficiary.Bytes;
    }

    public struct OpTimestamp : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp;
    }

    public struct OpNumber : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Number;
    }

    public struct OpGasLimit : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasLimit;
    }

    public struct OpBaseFee : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.BaseFeePerGas;
    }

    public struct OpBlobBaseFee : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.BlobBaseFee.Value;
    }

    public struct OpMSize : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Memory.Size;
    }
}
