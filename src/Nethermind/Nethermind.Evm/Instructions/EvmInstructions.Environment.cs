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
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
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

    public interface IOpEnvUInt32
    {
        virtual static long GasCost => GasCostOf.Base;
        abstract static uint Operation(EvmState vmState);
    }

    public interface IOpEnvUInt64
    {
        virtual static long GasCost => GasCostOf.Base;
        abstract static ulong Operation(EvmState vmState);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvBytes<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvBytes
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vm.EvmState, out Span<byte> result);

        stack.PushBytes(result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt256<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt256
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vm.EvmState, out UInt256 result);

        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt32<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt32
    {
        gasAvailable -= TOpEnv.GasCost;

        uint result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt32(result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt64<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt64
    {
        gasAvailable -= TOpEnv.GasCost;

        ulong result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt64(result);

        return EvmExceptionType.None;
    }

    public struct OpCallDataSize : IOpEnvUInt32
    {
        public static uint Operation(EvmState vmState)
            => (uint)vmState.Env.InputData.Length;
    }

    public struct OpCodeSize : IOpEnvUInt32
    {
        public static uint Operation(EvmState vmState)
            => (uint)vmState.Env.CodeInfo.MachineCode.Length;
    }

    public struct OpTimestamp : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp;
    }

    public struct OpNumber : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => (ulong)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Number;
    }

    public struct OpGasLimit : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => (ulong)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasLimit;
    }

    public struct OpMSize : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => vmState.Memory.Size;
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

    public struct OpGasPrice : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.GasPrice;
    }

    public struct OpCallValue : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.Value;
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

    public struct OpOrigin : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.Origin.Bytes;
    }

    public struct OpCoinbase : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasBeneficiary.Bytes;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionChainId(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;
        stack.PushBytes(vm.ChainId);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetBalanceCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        UInt256 result = vm.WorldState.GetBalance(address);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.SelfBalance;

        UInt256 result = vm.WorldState.GetBalance(vm.EvmState.Env.ExecutingAccount);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        if (state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            stack.PushBytes(state.GetCodeHash(address).Bytes);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHashEof(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        if (state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            Memory<byte> code = state.GetCode(address);
            if (EofValidator.IsEof(code, out _))
            {
                stack.PushBytes(EofHash256);
            }
            else
            {
                stack.PushBytes(state.GetCodeHash(address).Bytes);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    private static bool ChargeAccountAccessGasWithDelegation(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
        {
            return true;
        }
        bool notOutOfGas = ChargeAccountAccessGas(ref gasAvailable, vm, address, chargeForWarm);
        return notOutOfGas
               && (!vm.EvmState.Env.TxExecutionContext.CodeInfoRepository.TryGetDelegation(vm.WorldState, address, spec, out Address delegated)
                   || ChargeAccountAccessGas(ref gasAvailable, vm, delegated, chargeForWarm));
    }

    public static bool ChargeAccountAccessGas(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        bool result = true;
        IReleaseSpec spec = vm.Spec;
        if (spec.UseHotAndColdStorage)
        {
            EvmState vmState = vm.EvmState;
            if (vm.TxTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.AccessTracker.WarmUp(address);
            }

            if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec))
            {
                result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                vmState.AccessTracker.WarmUp(address);
            }
            else if (chargeForWarm)
            {
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }
}
