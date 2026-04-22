// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

public static partial class EvmInstructions
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
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the current program counter (not modified by this method).</param>
    /// <returns>
    /// An <see cref="EvmExceptionType"/> value indicating success or the type of error encountered.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
    {
        // Increment global call metrics.
        Metrics.IncrementCalls();

        // Clear previous return data.
        vm.ReturnData = null;

        // Pop the gas limit for the call.
        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        // Pop the code source address from the stack.
        Address codeSource = stack.PopAddress();
        if (codeSource is null) goto StackUnderflow;

        ExecutionEnvironment env = vm.VmState.Env;
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

        // Pop additional parameters: data offset, data length, output offset, and output length.
        if (!stack.PopUInt256(out UInt256 dataOffset, out UInt256 dataLength, out UInt256 outputOffset, out UInt256 outputLength))
        {
            goto StackUnderflow;
        }

        // For non-delegate calls, the transfer value is the call value.
        UInt256 transferValue = typeof(TOpCall) == typeof(OpDelegateCall) ? UInt256.Zero : callValue;
        // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
        if (vm.VmState.IsStatic && !transferValue.IsZero && typeof(TOpCall) != typeof(OpCallCode))
            return EvmExceptionType.StaticCallViolation;

        // Determine caller and target based on the call type.
        Address caller = typeof(TOpCall) == typeof(OpDelegateCall) ? env.Caller : env.ExecutingAccount;
        Address target = (typeof(TOpCall) == typeof(OpCall) || typeof(TOpCall) == typeof(OpStaticCall))
            ? codeSource
            : env.ExecutingAccount;

        // Add extra gas cost if value is transferred.
        if (!transferValue.IsZero)
        {
            if (!TGasPolicy.ConsumeCallValueTransfer(ref gas)) goto OutOfGas;
        }

        IReleaseSpec spec = vm.Spec;

        IWorldState state = vm.WorldState;

        // Update gas: call cost and memory expansion for input and output.
        if (!TGasPolicy.UpdateGas(ref gas, spec.GasCosts.CallCost) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataLength, vm.VmState) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in outputOffset, outputLength, vm.VmState))
            goto OutOfGas;

        // Charge gas for accessing the account's code (including delegation logic if applicable).
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, vm.Spec, in vm.VmState.AccessTracker,
                vm.TxTracer.IsTracingAccess, codeSource)) goto OutOfGas;
        bool _ = vm.TxExecutionContext.CodeInfoRepository
            .TryGetDelegation(codeSource, vm.Spec, out Address delegated);

        if (spec.UseHotAndColdStorage && delegated is not null)
        {
            if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, vm.Spec, in vm.VmState.AccessTracker,
                    vm.TxTracer.IsTracingAccess, delegated)) goto OutOfGas;
        }

        // Charge additional gas if the target account is new or considered empty.
        bool chargesNewAccount = spec.ClearEmptyAccountWhenTouched switch
        {
            false => !state.AccountExists(target),
            true => transferValue != 0 && state.IsDeadAccount(target),
        };

        bool newAccountOutOfGas = chargesNewAccount && !TGasPolicy.ConsumeNewAccountCreation<TEip8037>(ref gas);

        if (newAccountOutOfGas) goto OutOfGas;


        // Retrieve code information for the call and schedule background analysis if needed.
        CodeInfo codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(codeSource, spec);
        bool useFastPrecompile = codeInfo.IsPrecompile
            && codeInfo.Precompile!.SupportsFastPath
            && !TTracingInst.IsActive
            && !vm.TxTracer.IsTracingActions;

        // Get remaining gas for 63/64 calculation
        long gasAvailable = TGasPolicy.GetRemainingGas(in gas);

        // Apply the 63/64 gas rule if enabled.
        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        // If gasLimit exceeds the host's representable range, treat as out-of-gas.
        if (gasLimit >= long.MaxValue) goto OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!TGasPolicy.UpdateGas(ref gas, gasLimitUl)) goto OutOfGas;

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
            EvmExceptionType pushResult = stack.PushZero<TTracingInst>();

            // Optionally report memory changes for refund tracing.
            if (vm.TxTracer.IsTracingRefunds)
            {
                // Specific to Parity tracing: inspect 32 bytes from data offset.
                ReadOnlyMemory<byte>? memoryTrace = vm.VmState.Memory.Inspect(in dataOffset, 32);
                vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
            }

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(TGasPolicy.GetRemainingGas(in gas));
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            // Refund the remaining gas to the caller.
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, TGasPolicy.GetRemainingGas(in gas));
            }
            return pushResult;
        }

        if (useFastPrecompile)
        {
            return FastPrecompileCall(
                vm,
                state,
                ref gas,
                ref stack,
                gasLimitUl,
                in transferValue,
                caller,
                target,
                codeInfo.Precompile!,
                in dataOffset,
                in dataLength,
                in outputOffset,
                in outputLength,
                spec);
        }

        // Take a snapshot of the state for potential rollback.
        Snapshot snapshot = state.TakeSnapshot();
        // Subtract the transfer value from the caller's balance.
        state.SubtractFromBalance(caller, in transferValue, spec);

        // Fast-path for calls to externally owned accounts (non-contracts)
        if (codeInfo.IsEmpty && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            vm.ReturnDataBuffer = default;
            EvmExceptionType pushResult = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            if (pushResult != EvmExceptionType.None) return pushResult;
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            vm.AddTransferLog<TEip7708>(caller, target, transferValue);
            return FastCall(vm, spec, in transferValue, target);
        }

        // Load call data from memory.
        if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
            goto OutOfGas;
        // Construct the execution environment for the call.
        ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: target,
            caller: caller,
            codeSource: codeSource,
            callDepth: env.CallDepth + 1,
            transferValue: in transferValue,
            value: in callValue,
            inputData: in callData);

        // Normalize output offset if output length is zero.
        if (outputLength == 0)
        {
            // Output offset is inconsequential when output length is 0.
            outputOffset = 0;
        }

        // Rent a new call frame for executing the call.
        vm.ReturnData = VmState<TGasPolicy>.RentFrame(
            gas: TGasPolicy.CreateChildFrameGas(ref gas, gasLimitUl),
            outputDestination: outputOffset.ToLong(),
            outputLength: outputLength.ToLong(),
            executionType: TOpCall.ExecutionType,
            isStatic: TOpCall.IsStatic || vm.VmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            env: callEnv,
            stateForAccessLists: in vm.VmState.AccessTracker,
            snapshot: in snapshot);

        return EvmExceptionType.None;

        // Fast-call path for non-contract calls:
        // Directly credit the target account and avoid constructing a full call frame.
        static EvmExceptionType FastCall(VirtualMachine<TGasPolicy> vm, IReleaseSpec spec, in UInt256 transferValue, Address target)
        {
            IWorldState state = vm.WorldState;
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastPrecompileCall(
            VirtualMachine<TGasPolicy> vm,
            IWorldState state,
            ref TGasPolicy gas,
            ref EvmStack stack,
            long gasLimitUl,
            in UInt256 transferValue,
            Address caller,
            Address target,
            IPrecompile precompile,
            in UInt256 dataOffset,
            in UInt256 dataLength,
            in UInt256 outputOffset,
            in UInt256 outputLength,
            IReleaseSpec spec)
        {
            if (!vm.VmState.Memory.TryLoad(in dataOffset, in dataLength, out ReadOnlyMemory<byte> inputData))
                return EvmExceptionType.OutOfGas;

            long baseGasCost = precompile.BaseGasCost(spec);
            long dataGasCost = precompile.DataGasCost(inputData, spec);
            bool gasOverflow = (ulong)baseGasCost + (ulong)dataGasCost > (ulong)long.MaxValue;
            long precompileGasCost = gasOverflow ? long.MaxValue : baseGasCost + dataGasCost;

            Snapshot snapshot = default;
            bool hasSnapshot = false;
            if (!transferValue.IsZero)
            {
                hasSnapshot = true;
                snapshot = state.TakeSnapshot();
                state.SubtractFromBalance(caller, in transferValue, spec);
                state.AddToBalanceAndCreateIfNotExists(target, in transferValue, spec);
            }

            // On failure all forwarded gas is consumed, matching normal precompile CALL semantics.
            if (gasOverflow || precompileGasCost > gasLimitUl)
                return ReturnFailedPrecompileCallAndRestore(vm, ref stack, state, hasSnapshot, in snapshot);

            if (!TryRunPrecompile(vm, precompile, inputData, spec, out Result<byte[]> output) || !output)
                return ReturnFailedPrecompileCallAndRestore(vm, ref stack, state, hasSnapshot, in snapshot);

            EvmExceptionType precompileResult = HandlePrecompileSuccess(
                vm,
                ref gas,
                ref stack,
                output.Data,
                in outputOffset,
                in outputLength,
                gasLimitUl,
                precompileGasCost);

            if (precompileResult != EvmExceptionType.None)
                return precompileResult;

            vm.AddTransferLog<TEip7708>(caller, target, transferValue);

            // Mirror RunPrecompile account-touch behavior for zero-value successful precompile calls.
            // RIPEMD-160 is excluded from the fast path because its historical touch ordering matters.
            if (transferValue.IsZero)
                state.AddToBalanceAndCreateIfNotExists(target, in transferValue, spec);

            return EvmExceptionType.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool TryRunPrecompile(
            VirtualMachine<TGasPolicy> vm,
            IPrecompile precompile,
            ReadOnlyMemory<byte> inputData,
            IReleaseSpec spec,
            out Result<byte[]> output)
        {
            try
            {
                output = precompile.Run(inputData, spec);
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or { InnerException: DllNotFoundException })
            {
                if (vm.Logger.IsError)
                {
                    vm.Logger.Error(
                        $"Failed to load one of the dependencies of {precompile.GetType()} precompile",
                        exception as DllNotFoundException ?? exception.InnerException as DllNotFoundException);
                }

                Environment.Exit(ExitCodes.MissingPrecompile);
                throw;
            }
            catch (Exception exception)
            {
                if (vm.Logger.IsError)
                    vm.Logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);

                output = default;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType ReturnFailedPrecompileCall(VirtualMachine<TGasPolicy> vm, ref EvmStack stack)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            EvmExceptionType pushResult = stack.PushBytes<TTracingInst>(StatusCode.FailureBytes.Span);
            vm.ReturnData = null;
            return pushResult;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType ReturnFailedPrecompileCallAndRestore(
            VirtualMachine<TGasPolicy> vm,
            ref EvmStack stack,
            IWorldState state,
            bool hasSnapshot,
            in Snapshot snapshot)
        {
            if (hasSnapshot)
                state.Restore(snapshot);

            return ReturnFailedPrecompileCall(vm, ref stack);
        }

        static EvmExceptionType HandlePrecompileSuccess(
            VirtualMachine<TGasPolicy> vm,
            ref TGasPolicy gas,
            ref EvmStack stack,
            byte[]? outputData,
            in UInt256 outputOffset,
            in UInt256 outputLength,
            long gasLimitUl,
            long precompileGasCost)
        {
            byte[] returnBytes = outputData ?? Array.Empty<byte>();
            vm.ReturnDataBuffer = returnBytes;

            if (!outputLength.IsZero)
            {
                int bytesToCopy = Math.Min(returnBytes.Length, (int)outputLength);
                if (bytesToCopy > 0 &&
                    !vm.VmState.Memory.TrySave(in outputOffset, returnBytes.AsSpan(0, bytesToCopy)))
                    return EvmExceptionType.OutOfGas;
            }

            EvmExceptionType pushResult = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            if (pushResult != EvmExceptionType.None)
                return pushResult;

            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl - precompileGasCost);
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
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, an error such as <see cref="EvmExceptionType.StackUnderflow"/>,
    /// <see cref="EvmExceptionType.OutOfGas"/>, or <see cref="EvmExceptionType.BadInstruction"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn<TGasPolicy>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Pop memory position and length for the return data.
        if (!stack.PopUInt256(out UInt256 position, out UInt256 length))
            goto StackUnderflow;

        // Update the memory cost for the region being returned.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, in length, vm.VmState) ||
            !vm.VmState.Memory.TryLoad(in position, in length, out ReadOnlyMemory<byte> returnData))
        {
            goto OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
