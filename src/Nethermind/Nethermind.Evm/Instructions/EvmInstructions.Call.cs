// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;

internal sealed partial class EvmInstructions
{
    public interface IOpCall
    {
        virtual static bool IsStatic => false;
        abstract static ExecutionType ExecutionType { get; }
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TOpCall, TTracingInstructions>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCall : struct, IOpCall
        where TTracingInstructions : struct, IFlag
    {
        Metrics.IncrementCalls();

        IReleaseSpec spec = vm.Spec;
        vm.ReturnData = null;
        ref readonly ExecutionEnvironment env = ref vm.EvmState.Env;
        IWorldState state = vm.WorldState;

        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        Address codeSource = stack.PopAddress();
        if (codeSource is null) goto StackUnderflow;

        if (!ChargeAccountAccessGasWithDelegation(ref gasAvailable, vm, codeSource)) goto OutOfGas;

        UInt256 callValue;
        if (typeof(TOpCall) == typeof(OpStaticCall))
        {
            callValue = UInt256.Zero;
        }
        else if (typeof(TOpCall) == typeof(OpDelegateCall))
        {
            callValue = env.Value;
        }
        else if (!stack.PopUInt256(out callValue))
        {
            goto StackUnderflow;
        }

        UInt256 transferValue = typeof(TOpCall) == typeof(OpDelegateCall) ? UInt256.Zero : callValue;
        if (!stack.PopUInt256(out UInt256 dataOffset) ||
            !stack.PopUInt256(out UInt256 dataLength) ||
            !stack.PopUInt256(out UInt256 outputOffset) ||
            !stack.PopUInt256(out UInt256 outputLength)) goto StackUnderflow;

        if (vm.EvmState.IsStatic && !transferValue.IsZero && typeof(TOpCall) != typeof(OpCallCode)) return EvmExceptionType.StaticCallViolation;

        Address caller = typeof(TOpCall) == typeof(OpDelegateCall) ? env.Caller : env.ExecutingAccount;
        Address target = typeof(TOpCall) == typeof(OpCall) || typeof(TOpCall) == typeof(OpStaticCall)
            ? codeSource
            : env.ExecutingAccount;

        //if (typeof(TLogger) == typeof(IsTracing))
        //{
        //    TraceCallDetails(codeSource, ref callValue, ref transferValue, caller, target);
        //}

        long gasExtra = 0L;

        if (!transferValue.IsZero)
        {
            gasExtra += GasCostOf.CallValue;
        }

        if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vm.EvmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vm.EvmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) goto OutOfGas;

        ICodeInfo codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec);
        codeInfo.AnalyseInBackgroundIfRequired();

        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        if (gasLimit >= long.MaxValue) goto OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) goto OutOfGas;

        if (!transferValue.IsZero)
        {
            if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        if (env.CallDepth >= MaxCallDepth ||
            !transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();

            if (vm.TxTracer.IsTracingRefunds)
            {
                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                ReadOnlyMemory<byte>? memoryTrace = vm.EvmState.Memory.Inspect(in dataOffset, 32);
                vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
            }

            //if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace("FAIL - call depth");
            if (TTracingInstructions.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(gasAvailable);
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (TTracingInstructions.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            }
            return EvmExceptionType.None;
        }

        Snapshot snapshot = state.TakeSnapshot();
        state.SubtractFromBalance(caller, transferValue, spec);

        if (codeInfo.IsEmpty && !TTracingInstructions.IsActive && !vm.TxTracer.IsTracingActions)
        {
            // Non contract call, no need to construct call frame can just credit balance and return gas
            vm.ReturnDataBuffer = default;
            stack.PushBytes(StatusCode.SuccessBytes.Span);
            UpdateGasUp(gasLimitUl, ref gasAvailable);
            return FastCall(vm, spec, in transferValue, target);
        }

        ReadOnlyMemory<byte> callData = vm.EvmState.Memory.Load(in dataOffset, dataLength);
        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: caller,
            codeSource: codeSource,
            executingAccount: target,
            transferValue: transferValue,
            value: callValue,
            inputData: callData,
            codeInfo: codeInfo
        );
        //if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Tx call gas {gasLimitUl}");
        if (outputLength == 0)
        {
            // TODO: when output length is 0 outputOffset can have any value really
            // and the value does not matter and it can cause trouble when beyond long range
            outputOffset = 0;
        }
        vm.ReturnData = EvmState.RentFrame(
            gasLimitUl,
            outputOffset.ToLong(),
            outputLength.ToLong(),
            TOpCall.ExecutionType,
            TOpCall.IsStatic || vm.EvmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            snapshot: snapshot,
            env: callEnv,
            stateForAccessLists: vm.EvmState.AccessTracker);

        return EvmExceptionType.None;

        static EvmExceptionType FastCall(VirtualMachine vm, IReleaseSpec spec, in UInt256 transferValue, Address target)
        {
            IWorldState state = vm.WorldState;
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

    //[MethodImpl(MethodImplOptions.NoInlining)]
    //void TraceCallDetails(Address codeSource, ref UInt256 callValue, ref UInt256 transferValue, Address caller, Address target)
    //{
    //    _logger.Trace($"caller {caller}");
    //    _logger.Trace($"code source {codeSource}");
    //    _logger.Trace($"target {target}");
    //    _logger.Trace($"value {callValue}");
    //    _logger.Trace($"transfer value {transferValue}");
    //}
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    public struct OpCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALL;
    }

    public struct OpCallCode : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALLCODE;
    }

    public struct OpDelegateCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.DELEGATECALL;
    }

    public struct OpStaticCall : IOpCall
    {
        public static bool IsStatic => true;
        public static ExecutionType ExecutionType => ExecutionType.STATICCALL;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (vm.EvmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            goto BadInstruction;
        }

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            goto StackUnderflow;

        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in position, in length))
        {
            goto OutOfGas;
        }

        vm.ReturnData = vm.EvmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }
}
