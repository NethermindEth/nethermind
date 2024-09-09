// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.Precompiles;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPop(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;
        stack.PopLimbo();

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionChainId(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ChainIdOpcodeEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;
        stack.PushBytes(vm.ChainId);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetBalanceCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) return EvmExceptionType.OutOfGas;

        UInt256 result = vm.WorldState.GetBalance(address);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.ExtCodeHashOpcodeEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) return EvmExceptionType.OutOfGas;

        var state = vm.WorldState;
        if (!state.AccountExists(address) || state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            Span<byte> code = state.GetCode(address);
            if (spec.IsEofEnabled && EofValidator.IsEof(code, out _))
            {
                stack.PushBytes(EofHash256);
            }
            else
            {
                stack.PushBytes(state.GetCodeHash(address).Bytes);
            }
        }

        return EvmExceptionType.None;
    }


    [SkipLocalsInit]
    public static EvmExceptionType InstructionMLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        EvmState vmState = vm.State;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) return EvmExceptionType.OutOfGas;
        Span<byte> bytes = vmState.Memory.LoadSpan(in result);
        if (vm.TxTracer.IsTracingInstructions) vm.TxTracer.ReportMemoryChange(result, bytes);

        stack.PushBytes(bytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        Span<byte> bytes = stack.PopWord256();
        EvmState vmState = vm.State;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) return EvmExceptionType.OutOfGas;
        vmState.Memory.SaveWord(in result, bytes);
        if (vm.TxTracer.IsTracingInstructions) vm.TxTracer.ReportMemoryChange((long)result, bytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore8(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;

        byte data = stack.PopByte();
        EvmState vmState = vm.State;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in UInt256.One)) return EvmExceptionType.OutOfGas;
        vmState.Memory.SaveByte(in result, data);
        if (vm.TxTracer.IsTracingInstructions) vm.TxTracer.ReportMemoryChange((long)result, data);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.SelfBalance;

        UInt256 result = vm.WorldState.GetBalance(vm.State.Env.ExecutingAccount);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    public static bool ChargeAccountAccessGas(ref long gasAvailable, IEvm vm, Address address, bool chargeForWarm = true)
    {
        bool result = true;
        IReleaseSpec spec = vm.Spec;
        if (spec.UseHotAndColdStorage)
        {
            EvmState vmState = vm.State;
            if (vm.TxTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
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
    public static EvmExceptionType InstructionEnvBytes<TOpEnv>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvBytes
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vm.State, out Span<byte> result);

        stack.PushBytes(result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt256<TOpEnv>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt256
    {
        if (typeof(TOpEnv) == typeof(OpBaseFee) && !vm.Spec.BaseFeeEnabled) return EvmExceptionType.BadInstruction;
        if (typeof(TOpEnv) == typeof(OpBlobBaseFee) && !vm.Spec.BlobBaseFeeEnabled) return EvmExceptionType.BadInstruction;
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vm.State, out UInt256 result);

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
        {
            UInt256? blobBaseFee = vmState.Env.TxExecutionContext.BlockExecutionContext.BlobBaseFee;
            if (!blobBaseFee.HasValue) ThrowBadInstruction();

            result = blobBaseFee.Value;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowBadInstruction() => throw new BadInstructionException();
        }
    }

    public struct OpMSize : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Memory.Size;
    }
}
