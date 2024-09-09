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
    public static EvmExceptionType InstructionCall<TOpCall>(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCall : struct, IOpCall
    {
        Metrics.IncrementCalls();

        var spec = vm.Spec;
        vm.ReturnData = null;
        ref readonly ExecutionEnvironment env = ref vm.State.Env;
        IWorldState state = vm.WorldState;

        if (!stack.PopUInt256(out UInt256 gasLimit)) return EvmExceptionType.StackUnderflow;
        Address codeSource = stack.PopAddress();
        if (codeSource is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vm, codeSource)) return EvmExceptionType.OutOfGas;

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
            return EvmExceptionType.StackUnderflow;
        }

        UInt256 transferValue = typeof(TOpCall) == typeof(OpDelegateCall) ? UInt256.Zero : callValue;
        if (!stack.PopUInt256(out UInt256 dataOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 dataLength)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputLength)) return EvmExceptionType.StackUnderflow;

        if (vm.State.IsStatic && !transferValue.IsZero && typeof(TOpCall) == typeof(OpCallCode)) return EvmExceptionType.StaticCallViolation;

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
            !UpdateMemoryCost(vm.State, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vm.State, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        ICodeInfo codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec);
        codeInfo.AnalyseInBackgroundIfRequired();

        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        if (gasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (!transferValue.IsZero)
        {
            //if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        if (env.CallDepth >= MaxCallDepth ||
            !transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();

            //if (typeof(TTracingInstructions) == typeof(IsTracing))
            //{
            //    // very specific for Parity trace, need to find generalization - very peculiar 32 length...
            //    ReadOnlyMemory<byte>? memoryTrace = vm.State.Memory.Inspect(in dataOffset, 32);
            //    _txTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? ReadOnlySpan<byte>.Empty : memoryTrace.Value.Span);
            //}

            //if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace("FAIL - call depth");
            //if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationRemainingGas(gasAvailable);
            //if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            //if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            return EvmExceptionType.None;
        }

        Snapshot snapshot = state.TakeSnapshot();
        state.SubtractFromBalance(caller, transferValue, spec);

        if (codeInfo.IsEmpty /*&& typeof(TTracingInstructions) != typeof(IsTracing) && !_txTracer.IsTracingActions*/)
        {
            // Non contract call, no need to construct call frame can just credit balance and return gas
            vm.ReturnDataBuffer = default;
            stack.PushBytes(StatusCode.SuccessBytes.Span);
            UpdateGasUp(gasLimitUl, ref gasAvailable);
            return FastCall(vm, spec, in transferValue, target);
        }

        ReadOnlyMemory<byte> callData = vm.State.Memory.Load(in dataOffset, dataLength);
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

        vm.ReturnData = new EvmState(
            gasLimitUl,
            callEnv,
            TOpCall.ExecutionType,
            isTopLevel: false,
            snapshot,
            (long)outputOffset,
            (long)outputLength,
            TOpCall.IsStatic || vm.State.IsStatic,
            vm.State,
            isContinuation: false,
            isCreateOnPreExistingAccount: false);

        return EvmExceptionType.None;

        EvmExceptionType FastCall(IEvm vm, IReleaseSpec spec, in UInt256 transferValue, Address target)
        {
            IWorldState state = vm.WorldState;
            if (!state.AccountExists(target))
            {
                state.CreateAccount(target, transferValue);
            }
            else
            {
                state.AddToBalance(target, transferValue, spec);
            }
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
    public static EvmExceptionType InstructionReturn(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (vm.State.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            return EvmExceptionType.BadInstruction;
        }

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vm.State, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        vm.ReturnData = vm.State.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.None;


    }
}
