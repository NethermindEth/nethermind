// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

using System;

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

    [SkipLocalsInit]
    public static void InstructionEnvBytes<TOpEnv, TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TOpEnv : struct, IOpEnvBytes
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vmState, out Span<byte> result);

        stack.PushBytes(result);
    }

    [SkipLocalsInit]
    public static void InstructionEnvUInt256<TOpEnv, TTracingInstructions>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TOpEnv : struct, IOpEnvUInt256
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vmState, out UInt256 result);

        stack.PushUInt256(in result);
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

    //public struct OpCallDataLoad : IOpEnvironment
    //{
    //    public static long GasCost => GasCostOf.VeryLow;
    //    public static void Operation(EvmState vmState, out Span<byte> result)
    //        => result = vmState.Env.TxExecutionContext.Origin.Bytes;
    //}

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

    public struct OpCoinbase: IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasBeneficiary.Bytes;
    }

    public struct OpTimestamp: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp;
    }

    public struct OpNumber: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Number;
    }

    public struct OpGasLimit: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = (UInt256)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasLimit;
    }

    public struct OpBaseFee: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.BaseFeePerGas;
    }

    public struct OpBlobBaseFee: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.BlobBaseFee.Value;
    }

    public struct OpMSize: IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Memory.Size;
    }
}
