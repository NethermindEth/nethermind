// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface defining the properties for a call-like opcode.
    /// Each implementation specifies whether the call is static and what its execution type is.
    /// </summary>
    public interface IOpCall
    {
        /// <summary>
        /// Indicates if the call is static.
        /// Static calls cannot modify state.
        /// </summary>
        virtual static bool IsStatic => false;

        /// <summary>
        /// Returns the specific execution type of the call.
        /// </summary>
        abstract static ExecutionType ExecutionType { get; }
    }

    /// <summary>
    /// Represents a normal CALL opcode.
    /// </summary>
    public struct OpCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALL;
    }

    /// <summary>
    /// Represents a CALLCODE opcode.
    /// </summary>
    public struct OpCallCode : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALLCODE;
    }

    /// <summary>
    /// Represents a DELEGATECALL opcode.
    /// </summary>
    public struct OpDelegateCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.DELEGATECALL;
    }

    /// <summary>
    /// Represents a STATICCALL opcode.
    /// </summary>
    public struct OpStaticCall : IOpCall
    {
        public static bool IsStatic => true;
        public static ExecutionType ExecutionType => ExecutionType.STATICCALL;
    }

    /// <summary>
    /// Executes a call-like operation.
    /// This method handles various call types (CALL, CALLCODE, DELEGATECALL, STATICCALL) by:
    /// - Popping call parameters from the stack,
    /// - Charging appropriate gas for the call and memory expansion,
    /// - Validating call conditions (such as static call restrictions and call depth),
    /// - Performing balance transfers,
    /// - Creating a new execution frame for the call.
    /// </summary>
    /// <typeparam name="TOpCall">
    /// The call opcode type (e.g. <see cref="OpCall"/>, <see cref="OpStaticCall"/>).
    /// </typeparam>
    /// <typeparam name="TTracingInst">
    /// A type implementing <see cref="IFlag"/> that indicates whether instruction tracing is active.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance containing execution state.</param>
    /// <param name="stack">The EVM stack for retrieving call parameters and pushing results.</param>
    /// <param name="gasAvailable">Reference to the available gas, which is deducted according to various call costs.</param>
    /// <param name="programCounter">Reference to the current program counter (not modified by this method).</param>
    /// <returns>
    /// An <see cref="EvmExceptionType"/> value indicating success or the type of error encountered.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TOpCall, TTracingInst>(
        VirtualMachine vm,
        ref EvmStack stack,
        ref long gasAvailable,
        ref int programCounter)
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
    {
        // Increment global call metrics.
        Metrics.IncrementCalls();

        IReleaseSpec spec = vm.Spec;
        // Clear previous return data.
        vm.ReturnData = null;
        ref readonly ExecutionEnvironment env = ref vm.EvmState.Env;
        IWorldState state = vm.WorldState;

        // Pop the gas limit for the call.
        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        // Pop the code source address from the stack.
        Address codeSource = stack.PopAddress();
        if (codeSource is null) goto StackUnderflow;

        // Charge gas for accessing the account's code (including delegation logic if applicable).
        if (!ChargeAccountAccessGasWithDelegation(ref gasAvailable, vm, codeSource)) goto OutOfGas;

        // Determine the call value based on the call type.
        UInt256 callValue;
        if (typeof(TOpCall) == typeof(OpStaticCall))
        {
            // Static calls cannot transfer value.
            callValue = UInt256.Zero;
        }
        else if (typeof(TOpCall) == typeof(OpDelegateCall))
        {
            // Delegate calls use the value from the current execution context.
            callValue = env.Value;
        }
        else if (!stack.PopUInt256(out callValue))
        {
            goto StackUnderflow;
        }

        // For non-delegate calls, the transfer value is the call value.
        UInt256 transferValue = typeof(TOpCall) == typeof(OpDelegateCall) ? UInt256.Zero : callValue;
        // Pop additional parameters: data offset, data length, output offset, and output length.
        if (!stack.PopUInt256(out UInt256 dataOffset) ||
            !stack.PopUInt256(out UInt256 dataLength) ||
            !stack.PopUInt256(out UInt256 outputOffset) ||
            !stack.PopUInt256(out UInt256 outputLength))
            goto StackUnderflow;

        // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
        if (vm.EvmState.IsStatic && !transferValue.IsZero && typeof(TOpCall) != typeof(OpCallCode))
            return EvmExceptionType.StaticCallViolation;

        // Determine caller and target based on the call type.
        Address caller = typeof(TOpCall) == typeof(OpDelegateCall) ? env.Caller : env.ExecutingAccount;
        Address target = (typeof(TOpCall) == typeof(OpCall) || typeof(TOpCall) == typeof(OpStaticCall))
            ? codeSource
            : env.ExecutingAccount;

        long gasExtra = 0L;

        // Add extra gas cost if value is transferred.
        if (!transferValue.IsZero)
        {
            gasExtra += GasCostOf.CallValue;
        }

        // Charge additional gas if the target account is new or considered empty.
        if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        // Update gas: call cost, memory expansion for input and output, and extra gas.
        if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vm.EvmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vm.EvmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable))
            goto OutOfGas;

        // Retrieve code information for the call and schedule background analysis if needed.
        ICodeInfo codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec);

        // Apply the 63/64 gas rule if enabled.
        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        // If gasLimit exceeds the host's representable range, treat as out-of-gas.
        if (gasLimit >= long.MaxValue) goto OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) goto OutOfGas;

        // Add call stipend if value is being transferred.
        if (!transferValue.IsZero)
        {
            if (vm.TxTracer.IsTracingRefunds)
                vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        // Check call depth and balance of the caller.
        if (env.CallDepth >= MaxCallDepth ||
            (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
        {
            // If the call cannot proceed, return an empty response and push zero on the stack.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();

            // Optionally report memory changes for refund tracing.
            if (vm.TxTracer.IsTracingRefunds)
            {
                // Specific to Parity tracing: inspect 32 bytes from data offset.
                ReadOnlyMemory<byte>? memoryTrace = vm.EvmState.Memory.Inspect(in dataOffset, 32);
                vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
            }

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(gasAvailable);
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            // Refund the remaining gas to the caller.
            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            }
            return EvmExceptionType.None;
        }

        // Take a snapshot of the state for potential rollback.
        Snapshot snapshot = state.TakeSnapshot();
        // Subtract the transfer value from the caller's balance.
        state.SubtractFromBalance(caller, transferValue, spec);

        // Fast-path for calls to externally owned accounts (non-contracts)
        if (codeInfo.IsEmpty && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            vm.ReturnDataBuffer = default;
            stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            UpdateGasUp(gasLimitUl, ref gasAvailable);
            return FastCall(vm, spec, in transferValue, target);
        }

        // Load call data from memory.
        ReadOnlyMemory<byte> callData = vm.EvmState.Memory.Load(in dataOffset, dataLength);
        // Construct the execution environment for the call.
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

        // Normalize output offset if output length is zero.
        if (outputLength == 0)
        {
            // Output offset is inconsequential when output length is 0.
            outputOffset = 0;
        }

        // Rent a new call frame for executing the call.
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

        // Fast-call path for non-contract calls:
        // Directly credit the target account and avoid constructing a full call frame.
        static EvmExceptionType FastCall(VirtualMachine vm, IReleaseSpec spec, in UInt256 transferValue, Address target)
        {
            IWorldState state = vm.WorldState;
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    /// <summary>
    /// Executes the RETURN opcode.
    /// Pops a memory offset and a length from the stack, updates memory cost, and sets the return data.
    /// Returns an error if the opcode is executed in an invalid context.
    /// </summary>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the offset and length are popped.</param>
    /// <param name="gasAvailable">Reference to the available gas, adjusted by memory expansion cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, an error such as <see cref="EvmExceptionType.StackUnderflow"/>,
    /// <see cref="EvmExceptionType.OutOfGas"/>, or <see cref="EvmExceptionType.BadInstruction"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn(
        VirtualMachine vm,
        ref EvmStack stack,
        ref long gasAvailable,
        ref int programCounter)
    {
        // RETURN is not allowed during contract creation.
        if (vm.EvmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            goto BadInstruction;
        }

        // Pop memory position and length for the return data.
        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            goto StackUnderflow;

        // Update the memory cost for the region being returned.
        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in position, in length))
        {
            goto OutOfGas;
        }

        // Load the return data from memory and copy it to an array,
        // so the return value isn't referencing live memory,
        // which is being unwound in return.
        vm.ReturnData = vm.EvmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }
}
